using System.Security.Cryptography;
using System.Text;
using TerraScale.Dashboard.Features.Profile;
using TerraScale.Dashboard.Platform.Mcp;
using TerraScale.Dashboard.Shared.UI;

namespace TerraScale.Dashboard.Features.Auth;

// The dashboard signs in only through ts-auth's hosted pages, as a confidential
// OIDC client: authorization code with PKCE, state and nonce.
public static class AuthEndpoints
{
    public static void AddAuth(this IServiceCollection services)
    {
        services.AddSingleton<AuthFlows>();
        services.AddSingleton<IAuthEmailChangeGateway>(provider => provider.GetRequiredService<TsAuthGateway>());
        services.AddSingleton<IAuthStepUpGateway>(provider => provider.GetRequiredService<TsAuthGateway>());
        services.AddSingleton<TsAuthGateway>();
        services.AddSingleton<IAuthGateway>(provider => provider.GetRequiredService<TsAuthGateway>());
        services.AddSingleton<IAuthOidcGateway>(provider => provider.GetRequiredService<TsAuthGateway>());
        services.AddSingleton<IAuthRefreshGateway>(provider => provider.GetRequiredService<TsAuthGateway>());
        services.AddSingleton<ITsAuthMfa>(provider => provider.GetRequiredService<TsAuthGateway>());
        services.AddSingleton<ITsAuthProviders>(provider => provider.GetRequiredService<TsAuthGateway>());
        services.AddSingleton<IAuthLogoutGateway>(provider => provider.GetRequiredService<TsAuthGateway>());
        services.AddSingleton<IPlatformRoleGateway>(provider => provider.GetRequiredService<TsAuthGateway>());
        services.AddSingleton<IProjectAccessTokens>(provider => provider.GetRequiredService<TsAuthGateway>());
        services.AddSingleton<ITsAuthPlatform>(provider => provider.GetRequiredService<TsAuthGateway>());
        services.AddSingleton<ITsAuthProjects>(provider => provider.GetRequiredService<TsAuthGateway>());
        services.AddSingleton<ITsAuthDeletions>(provider => provider.GetRequiredService<TsAuthGateway>());
        services.AddHostedService<DeletionReconciler>();
    }

    public static void MapAuth(this WebApplication app)
    {
        app.MapMethods("/login", ["GET", "HEAD"], StartAsync);
        app.MapGet("/login/callback", CompleteAsync);
        app.MapGet("/auth/step-up/callback", StepUpFlow.CallbackAsync);
        app.MapGet("/organizations/choose", OrganizationPicker.PageAsync);
        app.MapPost("/api/v1/auth/logout", (Delegate)LogoutAsync);
    }

    // A signed-in visitor continues; everyone else goes to ts-auth's hosted sign-in, or with signup=1 its hosted sign-up.
    private static async Task<IResult> StartAsync(HttpContext context, DashboardSessions sessions)
    {
        var returnTo = Csrf.LocalRedirect(context.Request.Query["returnTo"].ToString());
        if (await sessions.ReadAsync(context) is not null) return HttpRedirects.SeeOther(returnTo);
        if (context.Request.Query["emailChanged"] == "1") return EmailChangedView.Create(new("Sign-in email changed", DocumentModel.NonceFor(context)));
        return await AuthorizeAsync(context, returnTo, signUp: context.Request.Query["signup"] == "1");
    }

    public static async Task<IResult> AuthorizeAsync(HttpContext context, string returnTo, string tokenName = "", int tokenDays = 0, bool signUp = false, TerraScale.Dashboard.Platform.Mcp.McpAuthorization? mcp = null, StepUpIntent? stepUp = null)
    {
        try
        {
            var flow = await context.RequestServices.GetRequiredService<AuthFlows>().StartOidcAsync(context, returnTo, tokenName, tokenDays, mcp, stepUp);
            var authorize = await context.RequestServices.GetRequiredService<IAuthOidcGateway>().AuthorizationUrlAsync(flow, context.RequestAborted);
            return Results.Redirect(signUp ? HostedSignUp(authorize) : authorize);
        }
        catch (Exception error) when (DependencyFailure(error, context))
        { return Failure(context, returnTo, "Sign-in is unavailable right now. Try again in a moment.", 503, error); }
    }

    // ts-auth's hosted sign-up page takes the same next=/{slug}/oauth/authorize?... as its sign-in page, so a new
    // customer lands on account creation instead of "Welcome back" and still returns through this flow.
    private static string HostedSignUp(string authorize)
    {
        var uri = new Uri(authorize);
        return uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries) is [var slug, "oauth", "authorize"]
            ? uri.GetLeftPart(UriPartial.Authority) + "/ui/" + slug + "/sign-up?next=" + Uri.EscapeDataString(uri.PathAndQuery)
            : authorize;
    }

    private static async Task<IResult> CompleteAsync(HttpContext context, AuthFlows flows, IAuthOidcGateway auth, DashboardSessions sessions)
    {
        var flow = await flows.TakeOidcAsync(context);
        var state = context.Request.Query["state"].ToString();
        if (flow is null || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(state), Encoding.UTF8.GetBytes(flow.State)))
            return Failure(context, "/", "This sign-in link expired or was started in another browser. Sign in again.", 400);
        if (context.Request.Query["error"].Count != 0)
            return flow.Mcp is not null ? McpOAuth.Denied(flow.Mcp) : Failure(context, flow.ReturnTo, "ts-auth did not sign you in. Sign in again.", 401);
        try
        {
            var session = await auth.ExchangeAsync(context.Request.Query["code"].ToString(), flow, context.RequestAborted);
            if (flow.TokenName.Length != 0) return await AccessTokenEndpoints.IssueAsync(context, flow, session);
            if (flow.Mcp is not null)
            {
                return await McpOAuth.GrantAsync(context, flow.Mcp, session, flow.StepUp);
            }
            var account = await EnsureAccountAsync(context, session);
            await sessions.StartAsync(context, session);
            var target = flow.ReturnTo == "/" && !account.OnboardingCompleted ? "/onboarding" : flow.ReturnTo;
            // API/CLI/MCP continuations retain their exact redirects. Organization choice is dashboard navigation.
            var offersChoice = !target.StartsWith("/api/", StringComparison.Ordinal) && !target.StartsWith("/onboarding", StringComparison.Ordinal) &&
                !target.StartsWith("/organizations/choose", StringComparison.Ordinal);
            // The audit write and the organization count are independent; the callback waits for the slower one only.
            var recorded = RecordSignInAsync(context, session, account);
            var workspaces = offersChoice ? context.RequestServices.GetRequiredService<AccountService>().GetContextAsync(session, context.RequestAborted) : null;
            await recorded;
            if (workspaces is not null && (await workspaces).Organizations.Count > 1)
                return HttpRedirects.SeeOther("/organizations/choose?returnTo=" + Uri.EscapeDataString(target));
            return HttpRedirects.SeeOther(target);
        }
        catch (AccountAccessException error) when (flow.StepUp is not null || flow.Mcp is not null)
        { return Failure(context, flow.ReturnTo, error.Message, error.StatusCode); }
        catch (SignInRejectedException)
        { return Failure(context, flow.ReturnTo, "ts-auth could not confirm this sign-in. Sign in again.", 401); }
        catch (TerraScale.DotNet.TerraScaleAuthException error) when (error.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
        {
            var seconds = Math.Clamp((int)Math.Ceiling(error.RetryAfter?.TotalSeconds ?? 60), 1, 3600);
            context.Response.Headers.RetryAfter = seconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return Failure(context, flow.ReturnTo, $"Too many sign-in attempts. Try again in {seconds} seconds.", 429, error);
        }
        catch (Exception error) when (DependencyFailure(error, context))
        { return Failure(context, flow.ReturnTo, "Sign-in could not finish right now. Try again in a moment.", 503, error); }
    }

    private static async Task<IResult> LogoutAsync(HttpContext context)
    {
        if (!context.Request.HasFormContentType) return Results.BadRequest();
        var form = await context.Request.ReadFormAsync(context.RequestAborted);
        if (!Csrf.Validate(context, form)) return Results.StatusCode(403);
        return await SignOutAsync(context);
    }

    // Ends the dashboard session and ts-auth's own, so the next /login shows the hosted sign-in
    // instead of silently signing the same account back in. The caller validates CSRF.
    public static async Task<IResult> SignOutAsync(HttpContext context)
    {
        var sessions = context.RequestServices.GetRequiredService<DashboardSessions>();
        var auth = context.RequestServices.GetRequiredService<IAuthGateway>();
        var logout = context.RequestServices.GetRequiredService<IAuthLogoutGateway>();
        var session = await sessions.ReadAsync(context);
        if (session is not null) await auth.RevokeAsync(session, context.RequestAborted);
        await sessions.EndAsync(context);
        return HttpRedirects.SeeOther(await logout.EndSessionUrlAsync($"{context.Request.Scheme}://{context.Request.Host}", session?.IdToken, context.RequestAborted));
    }

    // The sign-in joins the audit history of the organization it opens; ts-auth validates the membership.
    private static Task RecordSignInAsync(HttpContext context, SessionRecord session, AccountRecord account)
    {
        if (account.ActiveOrganizationId.Length == 0) return Task.CompletedTask;
        var time = context.RequestServices.GetRequiredService<TimeProvider>();
        return context.RequestServices.GetRequiredService<AccountService>().RecordAsync(new(session, account, [], new() { Id = account.ActiveOrganizationId }, null),
            org => org.AuditEvents.Add(new(AccountService.NewId(), account.Email, "session.created", account.Email, "session", time.GetUtcNow())), context.RequestAborted);
    }

    private static async Task<AccountRecord> EnsureAccountAsync(HttpContext context, SessionRecord session)
    {
        try { return await context.RequestServices.GetRequiredService<AccountService>().EnsureAsync(session, context.RequestAborted); }
        catch
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try { await context.RequestServices.GetRequiredService<IAuthGateway>().RevokeAsync(session, cleanup.Token); }
            catch (Exception error)
            {
                context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("DashboardAuth")
                    .LogWarning("Rejected account grant cleanup failed ({ErrorType})", error.GetType().Name);
            }
            throw;
        }
    }

    // A fixed page with a manual retry link: a failed callback never redirects back into ts-auth by itself.
    private static RazorSlices.RazorSlice Failure(HttpContext context, string returnTo, string error, int status, Exception? exception = null)
    {
        var reference = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(5));
        context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("DashboardAuth").LogWarning("ts-auth sign-in failed: {Reference} ({ErrorType})", reference, exception?.GetType().Name ?? "Rejected");
        var view = AuthFailureView.Create(new(new("Sign in", DocumentModel.NonceFor(context)), error, reference, Csrf.LocalRedirect(returnTo)));
        view.StatusCode = status;
        return view;
    }

    private static bool DependencyFailure(Exception error, HttpContext context) => error is HttpRequestException or IOException or System.Text.Json.JsonException or TimeoutException ||
        error is OperationCanceledException && !context.RequestAborted.IsCancellationRequested;
}

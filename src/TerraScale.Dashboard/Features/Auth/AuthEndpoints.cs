using System.Security.Cryptography;
using System.Text;
using TerraScale.Dashboard.Features.Accounts;
using TerraScale.Dashboard.Features.Profile;
using TerraScale.Dashboard.Shared.UI;

namespace TerraScale.Dashboard.Features.Auth;

// The dashboard signs in only through ts-auth's hosted pages, as a confidential
// OIDC client: authorization code with PKCE, state and nonce.
public static class AuthEndpoints
{
    public static void AddAuth(this IServiceCollection services)
    {
        services.AddSingleton<AuthFlows>();
        services.AddSingleton<TsAuthGateway>();
        services.AddSingleton<IAuthGateway>(provider => provider.GetRequiredService<TsAuthGateway>());
        services.AddSingleton<IAuthOidcGateway>(provider => provider.GetRequiredService<TsAuthGateway>());
        services.AddSingleton<IAuthRefreshGateway>(provider => provider.GetRequiredService<TsAuthGateway>());
        services.AddSingleton<IAuthLogoutGateway>(provider => provider.GetRequiredService<TsAuthGateway>());
        services.AddSingleton<IPlatformRoleGateway>(provider => provider.GetRequiredService<TsAuthGateway>());
        services.AddSingleton<IProjectAccessTokens>(provider => provider.GetRequiredService<TsAuthGateway>());
        services.AddSingleton<ITsAuthPlatform>(provider => provider.GetRequiredService<TsAuthGateway>());
    }

    public static void MapAuth(this WebApplication app)
    {
        app.MapGet("/login", StartAsync);
        app.MapGet("/login/callback", CompleteAsync);
        app.MapPost("/api/v1/auth/logout", LogoutAsync);
    }

    // A signed-in visitor continues; everyone else goes to ts-auth's hosted sign-in.
    private static async Task<IResult> StartAsync(HttpContext context, DashboardSessions sessions)
    {
        var returnTo = Csrf.LocalRedirect(context.Request.Query["returnTo"].ToString());
        if (await sessions.ReadAsync(context) is not null) return HttpRedirects.SeeOther(returnTo);
        return await AuthorizeAsync(context, returnTo);
    }

    public static async Task<IResult> AuthorizeAsync(HttpContext context, string returnTo, string tokenName = "", int tokenDays = 0)
    {
        try
        {
            var flow = await context.RequestServices.GetRequiredService<AuthFlows>().StartOidcAsync(context, returnTo, tokenName, tokenDays);
            return Results.Redirect(await context.RequestServices.GetRequiredService<IAuthOidcGateway>().AuthorizationUrlAsync(flow, context.RequestAborted));
        }
        catch (Exception error) when (DependencyFailure(error, context))
        { return Failure(context, returnTo, "Sign-in is unavailable right now. Try again in a moment.", 503, error); }
    }

    private static async Task<IResult> CompleteAsync(HttpContext context, AuthFlows flows, IAuthOidcGateway auth, DashboardSessions sessions)
    {
        var flow = await flows.TakeOidcAsync(context);
        var state = context.Request.Query["state"].ToString();
        if (flow is null || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(state), Encoding.UTF8.GetBytes(flow.State)))
            return Failure(context, "/", "This sign-in link expired or was started in another browser. Sign in again.", 400);
        if (context.Request.Query["error"].Count != 0)
            return Failure(context, flow.ReturnTo, "ts-auth did not sign you in. Sign in again.", 401);
        try
        {
            var session = await auth.ExchangeAsync(context.Request.Query["code"].ToString(), flow, context.RequestAborted);
            if (flow.TokenName.Length != 0) return await AccessTokenEndpoints.IssueAsync(context, flow, session);
            var account = await EnsureAccountAsync(context, session);
            await sessions.StartAsync(context, session);
            return HttpRedirects.SeeOther(flow.ReturnTo == "/" && !account.OnboardingCompleted ? "/onboarding" : flow.ReturnTo);
        }
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

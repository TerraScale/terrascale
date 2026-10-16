using System.Security.Cryptography;
using System.Text;
using TerraScale.Dashboard.Features.Accounts;
using TerraScale.Dashboard.Shared.UI;

namespace TerraScale.Dashboard.Features.Auth;

public static class EnrollmentEndpoints
{
    public static void AddEnrollmentAuth(this IServiceCollection services)
    {
        services.AddSingleton<AuthFlows>();
        services.AddSingleton<IAuthAttemptBudget, AuthAttemptBudget>();
        services.AddSingleton<TsAuthGateway>();
        services.AddSingleton<IAuthEnrollmentGateway>(provider => provider.GetRequiredService<TsAuthGateway>());
        services.AddSingleton<IAuthOidcGateway>(provider => provider.GetRequiredService<TsAuthGateway>());
        services.AddSingleton<IAuthRefreshGateway>(provider => provider.GetRequiredService<TsAuthGateway>());
        services.AddSingleton<IAuthLogoutGateway>(provider => provider.GetRequiredService<TsAuthGateway>());
        services.AddSingleton<IProfileIdentityGateway>(provider => provider.GetRequiredService<TsAuthGateway>());
        services.AddSingleton<IPlatformRoleGateway>(provider => provider.GetRequiredService<TsAuthGateway>());
        services.AddSingleton<IProjectAccessTokens>(provider => provider.GetRequiredService<TsAuthGateway>());
        services.AddSingleton<ITsAuthPlatform>(provider => provider.GetRequiredService<TsAuthGateway>());
        services.AddSingleton<DeviceGrants>();
        services.AddSingleton<DeviceAuthorization>();
        services.AddHostedService<DeviceCleanup>();
    }

    public static void MapEnrollmentAuth(this WebApplication app)
    {
        app.MapPost("/login/code/resend", ResendAsync);
        app.MapGet("/login/sso", StartSsoAsync);
        app.MapGet("/login/callback", CompleteSsoAsync);
        app.MapProfileIdentity();
        app.MapDeviceAuthorization();
        app.MapConsent();
    }

    public static AuthPage SignUpPage(HttpContext context, SignUpFlow flow, AuthFlows flows) => new(
        new DocumentModel("Verify your email", DocumentModel.NonceFor(context)), "code-verify", Csrf.Token(context),
        flow.Email, flow.ReturnTo, SignUp: true, Name: flow.Name, ResendSeconds: flows.Remaining(flow));

    public static string ClientIp(HttpContext context)
    {
        if (Environment.GetEnvironmentVariable("FLY_APP_NAME") is not null &&
            System.Net.IPAddress.TryParse(context.Request.Headers["Fly-Client-IP"], out var flyIp)) return flyIp.ToString();
        return context.Connection.RemoteIpAddress?.ToString() ?? "";
    }

    private static async Task<IResult> ResendAsync(HttpContext context, AuthFlows flows, IAuthEnrollmentGateway auth)
    {
        if (!context.Request.HasFormContentType) return Results.BadRequest();
        var form = await context.Request.ReadFormAsync(context.RequestAborted);
        if (!Csrf.Validate(context, form)) return Results.StatusCode(403);
        SignUpFlow? flow = null;
        try
        {
            flow = await flows.ReserveResendAsync(context);
            if (flow is null) return Expired(context);
            await context.RequestServices.GetRequiredService<IAuthAttemptBudget>().ReserveAsync(context, flow.Email, AuthBudgetKind.SignUpMail, context.RequestAborted);
            await flows.ReserveSignUpMailAsync(flow.Email, context.RequestAborted);
            var pending = await auth.SignUpAsync(flow.Email, flow.Password, ClientIp(context), context.RequestAborted);
            flow = flow with { PendingToken = pending };
            await flows.UpdateSignUpAsync(context, flow);
            return AuthView.Create(SignUpPage(context, flow, flows) with { Notice = "Check your email for a new code." });
        }
        catch (AuthRequestRejectedException error)
        {
            flow ??= await flows.ReadSignUpAsync(context);
            if (flow is null) return Expired(context);
            if (error.RetryAfter > 0) context.Response.Headers.RetryAfter = error.RetryAfter.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var view = AuthView.Create(SignUpPage(context, flow, flows) with { Error = error.Message, ResendSeconds = Math.Max(flows.Remaining(flow), error.RetryAfter) });
            view.StatusCode = error.StatusCode;
            return view;
        }
    }

    private static async Task<IResult> StartSsoAsync(HttpContext context, AuthFlows flows, IAuthOidcGateway auth)
    {
        var returnTo = Csrf.LocalRedirect(context.Request.Query["returnTo"].ToString());
        try
        {
            var flow = await flows.StartOidcAsync(context, returnTo);
            var target = await auth.AuthorizationUrlAsync(flow, context.RequestAborted);
            return AuthEndpoints.SeeOther(target);
        }
        catch (Exception error) when (DependencyFailure(error, context))
        { return Failure(context, returnTo, "Single sign-on is unavailable right now. Try again in a moment.", 503, error); }
    }

    private static async Task<IResult> CompleteSsoAsync(HttpContext context, AuthFlows flows, IAuthOidcGateway auth, DashboardSessions sessions)
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
            var account = await AuthEndpoints.EnsureAccountAsync(context, context.RequestServices.GetRequiredService<IAuthGateway>(), session);
            await sessions.StartAsync(context, session);
            return HttpRedirects.SeeOther(flow.ReturnTo == "/" && !account.OnboardingCompleted ? "/onboarding" : flow.ReturnTo);
        }
        catch (SignInRejectedException)
        { return Failure(context, flow.ReturnTo, "ts-auth could not confirm this sign-in. Sign in again.", 401); }
        catch (Exception error) when (DependencyFailure(error, context))
        { return Failure(context, flow.ReturnTo, "Sign-in could not finish right now. Try again in a moment.", 503, error); }
    }

    private static RazorSlices.RazorSlice Expired(HttpContext context)
    {
        var view = AuthView.Create(new(new("Verify your email", DocumentModel.NonceFor(context)), "signup-expired", Csrf.Token(context)));
        view.StatusCode = 400;
        return view;
    }

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

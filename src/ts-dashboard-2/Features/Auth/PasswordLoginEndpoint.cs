using System.Text.Json;
using TerraScale.Dashboard2;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard2.Features.Auth;

/// <summary>Signs in with email + password. Accepts the login form (redirects)
/// or a JSON body (returns the session payload), mirroring ts-dashboard's
/// /api/v1/auth/password/login contract. Live mode delegates credential
/// verification to ts-auth (the SDK's auth service) and keeps the resulting
/// token pair behind the opaque dashboard cookie.</summary>
public sealed class PasswordLoginEndpoint(DashboardStore store, DashboardSecurityOptions security, LiveAuthSessionService? liveSessions = null) : EndpointWithoutRequest<IResult>
{
    public const string DemoPassword = "terrascale";

    public override void Configure()
    {
        Post("/api/v1/auth/password/login");
        Summary("Signs in with email and password");
        Tags("Auth");
        AllowAnonymous();
    }

    public override async Task<IResult> HandleAsync(CancellationToken ct)
    {
        var request = HttpContext.Request;
        var isForm = request.HasFormContentType;
        string? email;
        string? password;
        string? redirectTo;

        if (isForm)
        {
            var form = await request.ReadFormAsync(ct);
            email = form["email"].ToString();
            password = form["password"].ToString();
            redirectTo = form["redirectTo"].ToString();
        }
        else
        {
            using var document = await JsonDocument.ParseAsync(request.Body, cancellationToken: ct);
            email = document.RootElement.TryGetProperty("email", out var e) ? e.GetString() : null;
            password = document.RootElement.TryGetProperty("password", out var p) ? p.GetString() : null;
            redirectTo = null;
        }

        if (!security.DemoMode)
        {
            var result = await liveSessions!.SignInAsync(email ?? "", password ?? "", ct);
            if (!result.Ok)
            {
                return isForm
                    ? Results.Redirect("/login?error=" + Uri.EscapeDataString(result.Error ?? "Incorrect email or password."))
                    : Results.Json(new { error = "invalid_credentials" }, statusCode: StatusCodes.Status401Unauthorized);
            }

            return SignInSucceeded(liveSessions.IssueCookie(result.Session!), isForm, redirectTo);
        }

        var world = store.FindAccount(email ?? "");
        if (world is null || !world.VerifyPassword(password ?? ""))
        {
            return isForm
                ? Results.Redirect("/login?error=" + Uri.EscapeDataString("Incorrect email or password."))
                : Results.Json(new { error = "invalid_credentials" }, statusCode: StatusCodes.Status401Unauthorized);
        }

        var token = store.BeginSession(world);
        return SignInSucceeded(token, isForm, redirectTo);
    }

    private IResult SignInSucceeded(string token, bool isForm, string? redirectTo)
    {
        HttpContext.Response.Cookies.Append(
            DashboardAuthMiddleware.SessionCookie,
            token,
            new CookieOptions { HttpOnly = true, SameSite = SameSiteMode.Lax, Path = "/" });
        if (isForm)
        {
            var target = redirectTo is { Length: > 0 } and var t && t.StartsWith('/') ? t : "/";
            return Results.Redirect(target, false, false);
        }

        return Results.Ok(new
        {
            user = new { store.CurrentUser.Email, store.CurrentUser.Name, onboardingCompleted = store.OnboardingCompleted },
        });
    }
}

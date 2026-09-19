using System.Text.Json;
using TerraScale.Dashboard2;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard2.Features.Auth;

/// <summary>Signs in with email + password. Accepts the login form (redirects)
/// or a JSON body (returns the session payload), mirroring ts-dashboard's
/// /api/v1/auth/password/login contract.</summary>
public sealed class PasswordLoginEndpoint(DashboardStore store) : EndpointWithoutRequest<IResult>
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

        if (email != store.CurrentUser.Email || !store.VerifyPassword(password ?? ""))
        {
            return isForm
                ? Results.Redirect("/login?error=" + Uri.EscapeDataString("Incorrect email or password."))
                : Results.Json(new { error = "invalid_credentials" }, statusCode: StatusCodes.Status401Unauthorized);
        }

        HttpContext.Response.Cookies.Append(
            DashboardAuthMiddleware.SessionCookie,
            DashboardAuthMiddleware.SessionToken,
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

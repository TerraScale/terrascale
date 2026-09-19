using TerraScale.Dashboard2;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard2.Features.Auth;

/// <summary>Demo passkey sign-in: WebAuthn is browser-side in ts-dashboard;
/// here the assertion is satisfied by the registered demo passkey.</summary>
public sealed class PasskeyLoginEndpoint(DashboardStore store) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Post("/api/v1/auth/passkey/login");
        Summary("Signs in with a passkey (demo)");
        Tags("Auth");
        AllowAnonymous();
    }

    public override Task<IResult> HandleAsync(CancellationToken ct)
    {
        store.AdoptExternalProvider("passkey");
        HttpContext.Response.Cookies.Append(
            DashboardAuthMiddleware.SessionCookie,
            DashboardAuthMiddleware.SessionToken,
            new CookieOptions { HttpOnly = true, SameSite = SameSiteMode.Lax, Path = "/" });
        return Task.FromResult<IResult>(Results.Redirect("/", false, false));
    }
}

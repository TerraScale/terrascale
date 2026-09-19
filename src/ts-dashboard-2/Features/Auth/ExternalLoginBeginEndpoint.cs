using TerraScale.Dashboard2;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard2.Features.Auth;

/// <summary>Demo external sign-in: the OAuth handshake is simulated by
/// adopting the provider identity on the demo user and starting a session,
/// the way ts-dashboard's Google/GitHub flows end up.</summary>
public sealed class ExternalLoginBeginEndpoint(DashboardStore store) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Get("/api/v1/auth/external-login/begin");
        Summary("Starts an external sign-in (demo: signs in directly)");
        Tags("Auth");
        AllowAnonymous();
    }

    public override Task<IResult> HandleAsync(CancellationToken ct)
    {
        var provider = HttpContext.Request.Query["provider"].ToString();
        if (provider is not ("google" or "github"))
        {
            return Task.FromResult<IResult>(Results.Redirect("/login?error=" + Uri.EscapeDataString("Unknown sign-in provider."), false, false));
        }

        store.AdoptExternalProvider(provider);
        HttpContext.Response.Cookies.Append(
            DashboardAuthMiddleware.SessionCookie,
            store.BeginSession(store.World),
            new CookieOptions { HttpOnly = true, SameSite = SameSiteMode.Lax, Path = "/" });
        return Task.FromResult<IResult>(Results.Redirect("/", false, false));
    }
}

using TerraScale.Dashboard;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Auth;

public sealed class LogoutEndpoint(DashboardStore store, DashboardSecurityOptions security, LiveAuthSessionService? liveSessions = null) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Post("/api/v1/auth/logout");
        Summary("Signs out of the dashboard session");
        Tags("Auth");
        AllowAnonymous();
    }

    public override async Task<IResult> HandleAsync(CancellationToken ct)
    {
        var cookie = HttpContext.Request.Cookies[DashboardAuthMiddleware.SessionCookie];
        if (!security.DemoMode)
        {
            if (liveSessions?.Lookup(cookie) is { } session)
            {
                await liveSessions.RevokeAsync(session, ct);
            }

            liveSessions?.Drop(cookie);
        }
        else
        {
            store.EndSession(cookie);
        }

        HttpContext.Response.Cookies.Delete(DashboardAuthMiddleware.SessionCookie);
        return Results.Redirect("/login", false, false);
    }
}

using TerraScale.Dashboard2;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard2.Features.Auth;

public sealed class LogoutEndpoint(DashboardStore store) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Post("/api/v1/auth/logout");
        Summary("Signs out of the dashboard session");
        Tags("Auth");
        AllowAnonymous();
    }

    public override Task<IResult> HandleAsync(CancellationToken ct)
    {
        store.EndSession(HttpContext.Request.Cookies[DashboardAuthMiddleware.SessionCookie]);
        HttpContext.Response.Cookies.Delete(DashboardAuthMiddleware.SessionCookie);
        return Task.FromResult<IResult>(Results.Redirect("/login", false, false));
    }
}

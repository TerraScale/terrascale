using TerraScale.Dashboard;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Organization;

/// <summary>Port of leaveOrganizationAction: membership ends, so the demo
/// session ends with it (the org picker is v1-only for now).</summary>
public sealed class LeaveOrganizationEndpoint : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Post("/api/v1/dashboard/organization/leave");
        Summary("Leaves the active organization");
        Tags("Organization");
    }

    public override Task<IResult> HandleAsync(CancellationToken ct)
    {
        HttpContext.Response.Cookies.Delete(DashboardAuthMiddleware.SessionCookie);
        return Task.FromResult<IResult>(Results.Redirect("/login", false, false));
    }
}

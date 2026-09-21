using TerraScale.Dashboard;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Organization;

public sealed class UpdateMemberRoleEndpoint(DashboardStore store) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Post("/api/v1/dashboard/organization/members/role");
        Summary("Changes an organization member role");
        Tags("Organization");
    }

    public override async Task<IResult> HandleAsync(CancellationToken ct)
    {
        var form = await HttpContext.Request.ReadFormAsync(ct);
        store.UpdateMemberRole(form["userId"].ToString(), form["role"].ToString());
        return Results.Redirect("/organization", false, false);
    }
}

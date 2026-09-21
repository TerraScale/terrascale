using TerraScale.Dashboard;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Access.Endpoints;

public sealed class UpdateMemberRoleEndpoint(DashboardStore store) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Post("/api/v1/access/members/{userId}/role");
        Summary("Changes a project team member role");
        Tags("Access");
    }

    public override async Task<IResult> HandleAsync(CancellationToken ct)
    {
        var userId = HttpContext.Request.RouteValues["userId"] as string ?? "";
        var form = await HttpContext.Request.ReadFormAsync(ct);
        store.UpdateMemberRole(userId, form["role"].ToString());
        return Results.Redirect("/access", false, false);
    }
}

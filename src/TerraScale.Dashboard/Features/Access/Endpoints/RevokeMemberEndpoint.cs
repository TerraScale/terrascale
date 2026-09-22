using TerraScale.Dashboard;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Access.Endpoints;

public sealed class RevokeMemberEndpoint(DashboardStore store) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Post("/api/v1/access/members/{userId}/revoke");
        Summary("Revokes a member's project access");
        Tags("Access");
    }

    public override Task<IResult> HandleAsync(CancellationToken ct)
    {
        var userId = HttpContext.Request.RouteValues["userId"] as string ?? "";
        store.RemoveMember(userId);
        return Task.FromResult(Results.Redirect("/access", false, false));
    }
}

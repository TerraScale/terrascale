using TerraScale.Dashboard;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Organization;

/// <summary>Port of removeOrganizationMemberAction.</summary>
public sealed class RemoveMemberEndpoint(DashboardStore store) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Post("/api/v1/dashboard/organization/members/remove");
        Summary("Removes a member from the organization");
        Tags("Organization");
    }

    public override async Task<IResult> HandleAsync(CancellationToken ct)
    {
        var form = await HttpContext.Request.ReadFormAsync(ct);
        store.RemoveMember(form["userId"].ToString());
        return Results.Redirect("/organization", false, false);
    }
}

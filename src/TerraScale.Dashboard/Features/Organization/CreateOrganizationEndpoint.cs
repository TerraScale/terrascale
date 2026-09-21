using TerraScale.Dashboard;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Organization;

/// <summary>Port of createOrganizationAction. The demo store is single-org,
/// so the new organization replaces the active organization identity before
/// the wizard continues to its first database.</summary>
public sealed class CreateOrganizationEndpoint(DashboardStore store) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Post("/api/v1/dashboard/organizations/create");
        Summary("Creates the organization and advances the wizard to payment");
        Tags("Organization");
    }

    public override async Task<IResult> HandleAsync(CancellationToken ct)
    {
        var form = await HttpContext.Request.ReadFormAsync(ct);
        store.RenameOrganization(form["name"].ToString());
        return Results.Redirect("/organizations/new?step=2", false, false);
    }
}

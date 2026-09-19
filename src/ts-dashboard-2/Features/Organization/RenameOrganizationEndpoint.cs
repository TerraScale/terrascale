using TerraScale.Dashboard2;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard2.Features.Organization;

/// <summary>Port of renameOrganizationAction.</summary>
public sealed class RenameOrganizationEndpoint(DashboardStore store) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Post("/api/v1/dashboard/organization/rename");
        Summary("Renames the active organization");
        Tags("Organization");
    }

    public override async Task<IResult> HandleAsync(CancellationToken ct)
    {
        var form = await HttpContext.Request.ReadFormAsync(ct);
        store.RenameOrganization(form["name"].ToString());
        return Results.Redirect("/organization", false, false);
    }
}

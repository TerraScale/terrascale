using TerraScale.Dashboard2;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard2.Features.Organization;

/// <summary>Port of requestOrganizationDeletionAction: requires typing the
/// organization name, then records the deletion request for review.</summary>
public sealed class RequestOrganizationDeletionEndpoint(DashboardStore store) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Post("/api/v1/dashboard/organization/deletion/request");
        Summary("Records the organization deletion request");
        Tags("Organization");
    }

    public override async Task<IResult> HandleAsync(CancellationToken ct)
    {
        var form = await HttpContext.Request.ReadFormAsync(ct);
        if (form["confirmation"].ToString().Trim() != store.ActiveOrganizationName)
        {
            return Results.Redirect("/organization?error=" + Uri.EscapeDataString("Organization name does not match."), false, false);
        }

        store.RequestOrganizationDeletion();
        return Results.Redirect("/organization", false, false);
    }
}

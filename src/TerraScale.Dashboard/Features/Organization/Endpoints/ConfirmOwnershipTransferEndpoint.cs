using TerraScale.Dashboard;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Organization.Endpoints;

/// <summary>Port of confirmOwnershipTransferAction: the target becomes the
/// owner and the signed-in owner drops to member, like the v1 email-code
/// confirmation (the demo store accepts any submitted code).</summary>
public sealed class ConfirmOwnershipTransferEndpoint(DashboardStore store) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Post("/api/v1/dashboard/organization/members/ownership/confirm");
        Summary("Confirms an ownership transfer");
        Tags("Organization");
    }

    public override async Task<IResult> HandleAsync(CancellationToken ct)
    {
        var form = await HttpContext.Request.ReadFormAsync(ct);
        if (form["code"].ToString().Trim().Length > 0)
        {
            store.ConfirmOwnershipTransfer(form["userId"].ToString());
        }

        return Results.Redirect("/organization", false, false);
    }
}

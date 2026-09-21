using TerraScale.Dashboard;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Organization;

/// <summary>Port of resendOrganizationInvitationAction: refreshes the pending
/// invitation in the demo store.</summary>
public sealed class ResendInvitationEndpoint(DashboardStore store) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Post("/api/v1/dashboard/organization/invitations/resend");
        Summary("Resends a pending invitation");
        Tags("Organization");
    }

    public override async Task<IResult> HandleAsync(CancellationToken ct)
    {
        var form = await HttpContext.Request.ReadFormAsync(ct);
        store.ResendInvitation(form["invitationId"].ToString());
        return Results.Redirect("/organization", false, false);
    }
}

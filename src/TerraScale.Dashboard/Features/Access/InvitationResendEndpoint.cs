using TerraScale.Dashboard;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Access;

/// <summary>Port of resendOrganizationInvitationAction: re-issues the
/// pending invitation with a fresh timestamp.</summary>
public sealed class InvitationResendEndpoint(DashboardStore store) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Post("/api/v1/access/invitations/{invitationId}/resend");
        Summary("Resends a pending invitation");
        Tags("Access");
    }

    public override Task<IResult> HandleAsync(CancellationToken ct)
    {
        store.ResendInvitation(HttpContext.Request.RouteValues["invitationId"]?.ToString() ?? "");
        return Task.FromResult<IResult>(Results.Redirect("/access", false, false));
    }
}

using TerraScale.Dashboard2;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard2.Features.Access;

public sealed class RevokeInvitationEndpoint(DashboardStore store) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Post("/api/v1/access/invitations/{invitationId}/revoke");
        Summary("Cancels a pending organization invitation");
        Tags("Access");
    }

    public override Task<IResult> HandleAsync(CancellationToken ct)
    {
        var invitationId = HttpContext.Request.RouteValues["invitationId"] as string ?? "";
        store.RevokeInvitation(invitationId);
        return Task.FromResult(Results.Redirect("/access", false, false));
    }
}

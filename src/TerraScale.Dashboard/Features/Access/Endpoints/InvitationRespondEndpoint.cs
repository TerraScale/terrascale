using TerraScale.Dashboard;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Access.Endpoints;

public sealed class InvitationRespondEndpoint(DashboardStore store) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Post("/invitations/{invitationId}/respond");
        Summary("Accepts or declines an organization invitation");
        Tags("Access");
        AllowAnonymous();
    }

    public override async Task<IResult> HandleAsync(CancellationToken ct)
    {
        var form = await HttpContext.Request.ReadFormAsync(ct);
        var id = HttpContext.Request.RouteValues["invitationId"] as string ?? "";
        var action = form["action"].ToString();
        var invitation = store.Invitations.FirstOrDefault(i => i.Id == id || i.Token == id);
        if (invitation is null || action is not ("accept" or "decline"))
        {
            return Results.Redirect($"/invitations/{Uri.EscapeDataString(id)}?error=" + Uri.EscapeDataString("This invitation link is missing its organization context."), false, false);
        }

        store.RespondToInvitation(invitation.Id, action == "accept" ? "Accepted" : "Declined");
        return Results.Redirect($"/invitations/{invitation.Id}?outcome={(action == "accept" ? "accepted" : "declined")}", false, false);
    }
}

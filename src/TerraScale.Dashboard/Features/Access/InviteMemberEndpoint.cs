using TerraScale.Dashboard;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Access;

public sealed class InviteMemberEndpoint(DashboardStore store) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Post("/api/v1/access/invitations");
        Summary("Invites a member to the organization by email");
        Tags("Access");
    }

    public override async Task<IResult> HandleAsync(CancellationToken ct)
    {
        var form = await HttpContext.Request.ReadFormAsync(ct);
        var email = form["email"].ToString().Trim();
        if (email.Length == 0 || !email.Contains('@'))
        {
            return Results.Redirect("/access?error=" + Uri.EscapeDataString("A valid invite email is required."), false, false);
        }

        store.InviteMember(email, "member");
        return Results.Redirect("/access", false, false);
    }
}

using TerraScale.Dashboard;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Organization.Endpoints;

public sealed class CreateInvitationEndpoint(DashboardStore store) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Post("/api/v1/dashboard/organization/invitations");
        Summary("Sends an organization invitation");
        Tags("Organization");
    }

    public override async Task<IResult> HandleAsync(CancellationToken ct)
    {
        var form = await HttpContext.Request.ReadFormAsync(ct);
        var email = form["email"].ToString().Trim();
        if (email.Length > 0)
        {
            store.InviteMember(email, "member");
        }

        return Results.Redirect("/organization", false, false);
    }
}

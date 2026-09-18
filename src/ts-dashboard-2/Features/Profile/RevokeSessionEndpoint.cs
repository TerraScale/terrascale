using TerraScale.Dashboard2;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard2.Features.Profile;

public sealed class RevokeSessionEndpoint(DashboardStore store) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Post("/api/v1/dashboard/profile/sessions/revoke");
        Summary("Revokes one active session");
        Tags("Profile");
    }

    public override async Task<IResult> HandleAsync(CancellationToken ct)
    {
        var form = await HttpContext.Request.ReadFormAsync(ct);
        store.RevokeSession(form["sessionId"].ToString());
        return Results.Redirect("/profile?tab=sessions", false, false);
    }
}

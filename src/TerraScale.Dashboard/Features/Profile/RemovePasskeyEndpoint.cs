using TerraScale.Dashboard;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Profile;

public sealed class RemovePasskeyEndpoint(DashboardStore store) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Post("/api/v1/dashboard/profile/passkeys/remove");
        Summary("Removes a registered passkey");
        Tags("Profile");
    }

    public override async Task<IResult> HandleAsync(CancellationToken ct)
    {
        var form = await HttpContext.Request.ReadFormAsync(ct);
        store.RemovePasskey(form["passkeyId"].ToString());
        return Results.Redirect("/profile?tab=security", false, false);
    }
}

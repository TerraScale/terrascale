using TerraScale.Dashboard;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Profile;

/// <summary>Port of passkey registration (demo: the platform authenticator
/// attestation is satisfied by naming the device).</summary>
public sealed class RegisterPasskeyEndpoint(DashboardStore store) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Post("/api/v1/dashboard/profile/passkeys/register");
        Summary("Registers a passkey for this device or key");
        Tags("Profile");
    }

    public override async Task<IResult> HandleAsync(CancellationToken ct)
    {
        var form = await HttpContext.Request.ReadFormAsync(ct);
        store.RegisterPasskey(form["name"].ToString());
        return Results.Redirect("/profile?tab=security", false, false);
    }
}

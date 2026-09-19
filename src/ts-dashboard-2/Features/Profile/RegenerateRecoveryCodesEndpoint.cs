using TerraScale.Dashboard2;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard2.Features.Profile;

/// <summary>Port of the recovery-codes regeneration: requires the current
/// password; the new set is revealed once on the next render.</summary>
public sealed class RegenerateRecoveryCodesEndpoint(DashboardStore store) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Post("/api/v1/dashboard/profile/mfa/recovery-codes");
        Summary("Regenerates the MFA recovery codes");
        Tags("Profile");
    }

    public override async Task<IResult> HandleAsync(CancellationToken ct)
    {
        var form = await HttpContext.Request.ReadFormAsync(ct);
        if (!store.VerifyPassword(form["password"].ToString()))
        {
            return Results.Redirect("/profile?tab=security&error=" + Uri.EscapeDataString("Incorrect password."), false, false);
        }

        store.RegenerateRecoveryCodes();
        return Results.Redirect("/profile?tab=security", false, false);
    }
}

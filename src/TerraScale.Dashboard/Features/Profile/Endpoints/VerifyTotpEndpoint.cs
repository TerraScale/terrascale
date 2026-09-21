using TerraScale.Dashboard;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Profile.Endpoints;

/// <summary>Port of the TOTP verification step: a 6-digit code from the
/// authenticator confirms the factor (demo: any 6 digits verify).</summary>
public sealed class VerifyTotpEndpoint(DashboardStore store) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Post("/api/v1/dashboard/profile/mfa/verify");
        Summary("Verifies a TOTP authenticator enrollment");
        Tags("Profile");
    }

    public override async Task<IResult> HandleAsync(CancellationToken ct)
    {
        var form = await HttpContext.Request.ReadFormAsync(ct);
        var token = form["enrollment"].ToString();
        var code = form["code"].ToString().Trim();
        if (code.Length != 6 || !code.All(char.IsDigit) || store.FindTotpEnrollment(token) is null)
        {
            return Results.Redirect("/profile?tab=security&error=" + Uri.EscapeDataString("MFA verification failed."), false, false);
        }

        store.CompleteTotpEnrollment(token);
        return Results.Redirect("/profile?tab=security", false, false);
    }
}

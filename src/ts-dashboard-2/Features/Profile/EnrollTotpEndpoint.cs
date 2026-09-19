using TerraScale.Dashboard2;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard2.Features.Profile;

/// <summary>Port of the TOTP enrollment step: password + a code from the
/// authenticator adds the factor (demo: any 6-digit code verifies).</summary>
public sealed class EnrollTotpEndpoint(DashboardStore store) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Post("/api/v1/dashboard/profile/mfa/enroll");
        Summary("Enrolls a TOTP authenticator factor");
        Tags("Profile");
    }

    public override async Task<IResult> HandleAsync(CancellationToken ct)
    {
        var form = await HttpContext.Request.ReadFormAsync(ct);
        var code = form["code"].ToString().Trim();
        if (!store.VerifyPassword(form["password"].ToString()))
        {
            return Results.Redirect("/profile?tab=security&error=" + Uri.EscapeDataString("Incorrect password."), false, false);
        }

        if (code.Length != 6 || !code.All(char.IsDigit))
        {
            return Results.Redirect("/profile?tab=security&error=" + Uri.EscapeDataString("MFA verification failed."), false, false);
        }

        store.EnrollTotp();
        return Results.Redirect("/profile?tab=security", false, false);
    }
}

using TerraScale.Dashboard;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Profile.Endpoints;

/// <summary>Port of the TOTP enrollment start: after re-authentication the
/// page shows the authenticator secret and the verification form.</summary>
public sealed class EnrollTotpEndpoint(DashboardStore store) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Post("/api/v1/dashboard/profile/mfa/enroll");
        Summary("Starts a TOTP authenticator enrollment");
        Tags("Profile");
    }

    public override async Task<IResult> HandleAsync(CancellationToken ct)
    {
        var form = await HttpContext.Request.ReadFormAsync(ct);
        if (!store.VerifyPassword(form["password"].ToString()))
        {
            return Results.Redirect("/profile?tab=security&error=" + Uri.EscapeDataString("Incorrect password."), false, false);
        }

        var enrollment = store.BeginTotpEnrollment();
        return Results.Redirect($"/profile?tab=security&enroll={enrollment.Token}", false, false);
    }
}

using TerraScale.Dashboard;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Profile.Endpoints;

/// <summary>Port of initiateEmailChangeAction. The demo store has no mailer,
/// so the verified address is applied directly to the session user.</summary>
public sealed class RequestEmailChangeEndpoint(DashboardStore store) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Post("/api/v1/dashboard/profile/email-change");
        Summary("Starts an email address change");
        Tags("Profile");
    }

    public override async Task<IResult> HandleAsync(CancellationToken ct)
    {
        var form = await HttpContext.Request.ReadFormAsync(ct);
        var newEmail = form["newEmail"].ToString().Trim();
        if (!store.VerifyPassword(form["password"].ToString()) || !newEmail.Contains('@') || !newEmail.Contains('.'))
        {
            return Results.Redirect("/profile?tab=profile&error=" + Uri.EscapeDataString("Enter a valid email and current password."), false, false);
        }

        store.ChangeEmail(newEmail);
        return Results.Redirect("/profile?tab=profile", false, false);
    }
}

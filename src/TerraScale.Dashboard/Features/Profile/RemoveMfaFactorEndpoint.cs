using TerraScale.Dashboard;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Profile;

public sealed class RemoveMfaFactorEndpoint(DashboardStore store) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Post("/api/v1/dashboard/profile/mfa/remove");
        Summary("Removes the TOTP authenticator factor");
        Tags("Profile");
    }

    public override async Task<IResult> HandleAsync(CancellationToken ct)
    {
        var form = await HttpContext.Request.ReadFormAsync(ct);
        var factor = form["factor"].ToString();
        if (!store.VerifyPassword(form["password"].ToString()))
        {
            return Results.Redirect("/profile?tab=security&error=" + Uri.EscapeDataString("Incorrect password."), false, false);
        }

        if (factor.Length > 0)
        {
            store.RemoveMfaFactor(factor);
        }

        return Results.Redirect("/profile?tab=security", false, false);
    }
}

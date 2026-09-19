using TerraScale.Dashboard2;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard2.Features.Profile;

/// <summary>Port of changePasswordAction: verifies the current password,
/// stores the new one, and terminates the session.</summary>
public sealed class ChangePasswordEndpoint(DashboardStore store) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Post("/api/v1/dashboard/profile/password");
        Summary("Changes the account password and terminates sessions");
        Tags("Profile");
    }

    public override async Task<IResult> HandleAsync(CancellationToken ct)
    {
        var form = await HttpContext.Request.ReadFormAsync(ct);
        var current = form["currentPassword"].ToString();
        var next = form["newPassword"].ToString();
        if (!store.VerifyPassword(current))
        {
            return Results.Redirect("/profile?tab=security&error=" + Uri.EscapeDataString("Current password is incorrect."), false, false);
        }

        if (next.Length < 8)
        {
            return Results.Redirect("/profile?tab=security&error=" + Uri.EscapeDataString("Use at least 8 characters."), false, false);
        }

        store.ChangePassword(current, next);
        HttpContext.Response.Cookies.Delete(DashboardAuthMiddleware.SessionCookie);
        return Results.Redirect("/login", false, false);
    }
}

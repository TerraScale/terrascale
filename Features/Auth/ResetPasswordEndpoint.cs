using TerraScale.Dashboard2;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard2.Features.Auth;

/// <summary>Accepts the reset-password form. Demo flow: request mode redirects
/// back with <c>?sent=1</c>; token mode accepts any 8+ char password and
/// redirects to the login page.</summary>
public sealed class ResetPasswordEndpoint : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Post("/reset-password");
        Summary("Submits a password reset request or a new password");
        Tags("Auth");
        AllowAnonymous();
    }

    public override async Task<IResult> HandleAsync(CancellationToken ct)
    {
        var form = await HttpContext.Request.ReadFormAsync(ct);
        var token = form["token"].ToString();
        var password = form["newPassword"].ToString();

        if (token.Length > 0)
        {
            if (password.Length < 8)
            {
                return Results.Redirect("/reset-password?token=" + Uri.EscapeDataString(token) + "&error=" + Uri.EscapeDataString("Password must be at least 8 characters."), false, false);
            }

            return Results.Redirect("/login", false, false);
        }

        return Results.Redirect("/reset-password?sent=1", false, false);
    }
}

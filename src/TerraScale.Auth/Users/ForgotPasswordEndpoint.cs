namespace TerraScale.Auth.Users;

using System.Net;
using TerraScale.Auth.Billing;
using TerraScale.Auth.Configuration;
using TerraScale.Auth.Crypto;
using TerraScale.Auth.Email;
using TerraScale.Auth.Mfa;
using TerraScale.Auth.Store;
using TerraScale.Auth.Ui;
using TerraScale.DotNet;
using TerraScale.Endpoints;
using static TerraScale.Auth.Users.UserFlow;

internal sealed class ForgotPasswordEndpoint : IEndpoint
{
    public RouteHandlerBuilder Map(IEndpointRouteBuilder routes) =>
        routes.MapMethods("/ui/{slug}/forgot-password", [HttpMethods.Get, HttpMethods.Post], HandleAsync);

    private static async Task<IResult> HandleAsync(string slug, HttpRequest request, AuthStore store, IEmailSender emailSender, AuthOptions options, CancellationToken ct)
    {
        var instance = await PageInstanceAsync(store, slug, ct);
        if (instance is null)
            return NotFoundPage();

        if (HttpMethods.IsGet(request.Method))
            return ForgotPasswordPage(slug, instance);

        var form = await request.ReadFormAsync(ct);
        var email = form["email"].ToString().Trim();
        if (SessionService.IsValidEmail(email))
        {
            var user = await store.FindUserByEmailAsync(instance.Id, SessionService.NormalizeEmail(email), ct);
            if (user is not null && !user.Deleted && !user.Banned)
            {
                var token = await IssueTokenAsync(store, instance.Id, user.Id, "reset_password", ResetPasswordTtl, ct);
                await emailSender.SendAsync(user.Email!, "Reset your password",
                    $"Reset your {instance.Name} password by opening:\n\n{Link(options, $"/ui/{slug}/reset-password", token)}\n\nThis link expires in 30 minutes and can only be used once. If you didn't request this, ignore this email.", ct);
            }
        }
        return CheckEmailPage(slug, instance, email, "password reset link", minutes: 30);
    }

    private static readonly TimeSpan ResetPasswordTtl = TimeSpan.FromMinutes(30);

    private static IResult ForgotPasswordPage(string slug, Instance brand) =>
        Ui.Page("Reset your password", $"""
            <h1>Reset your password</h1>
            <p class="muted">Enter your email and we'll send a reset link.</p>
            <form method="post">
            <label for="fp-email">Email</label>
            <input id="fp-email" name="email" type="email" required autofocus>
            <button type="submit">Send reset link</button>
            </form>
            <p class="hint"><a href="/ui/{slug}/sign-in">Back to sign in</a></p>
            """, themeColor: brand.ThemeColor, logoUrl: brand.LogoUrl);
}

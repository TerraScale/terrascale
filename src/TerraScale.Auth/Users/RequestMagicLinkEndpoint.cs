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

internal sealed class RequestMagicLinkEndpoint : IEndpoint
{
    public RouteHandlerBuilder Map(IEndpointRouteBuilder routes) =>
        routes.MapPost("/ui/{slug}/magic-link", HandleAsync);

    private static async Task<IResult> HandleAsync(string slug, HttpRequest request, AuthStore store, IEmailSender emailSender, AuthOptions options, CancellationToken ct)
    {
        var instance = await PageInstanceAsync(store, slug, ct);
        if (instance is null)
            return NotFoundPage();

        var form = await request.ReadFormAsync(ct);
        var email = form["email"].ToString().Trim();
        if (SessionService.IsValidEmail(email))
        {
            var user = await store.FindUserByEmailAsync(instance.Id, SessionService.NormalizeEmail(email), ct);
            if (user is not null && !user.Deleted && !user.Banned)
            {
                var token = await IssueTokenAsync(store, instance.Id, user.Id, "magic_link", MagicLinkTtl, ct);
                await emailSender.SendAsync(user.Email!, $"Your {instance.Name} sign-in link",
                    $"Sign in to {instance.Name} by opening:\n\n{Link(options, $"/ui/{slug}/magic-link/consume", token)}\n\nThis link expires in 10 minutes and can only be used once.", ct);
            }
        }
        return CheckEmailPage(slug, instance, email, "sign-in link", minutes: 10);
    }

    private static readonly TimeSpan MagicLinkTtl = TimeSpan.FromMinutes(10);
}

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

internal sealed class VerifyEmailEndpoint : IEndpoint
{
    public RouteHandlerBuilder Map(IEndpointRouteBuilder routes) =>
        routes.MapGet("/ui/{slug}/verify-email", HandleAsync);

    private static async Task<IResult> HandleAsync(string slug, HttpRequest request, AuthStore store, CancellationToken ct)
    {
        var instance = await PageInstanceAsync(store, slug, ct);
        if (instance is null)
            return NotFoundPage();

        var record = await ConsumeTokenAsync(store, instance.Id, request.Query["token"].ToString(), "verify_email", ct);
        var user = record?.UserId is { } userId ? await store.GetUserAsync(userId, ct) : null;
        if (user is null)
            return InvalidLinkPage(slug, instance);

        await store.UpsertUserAsync(user with
        {
            EmailVerified = true,
            UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        }, ct);
        await AuditAsync(store, instance.Id, user.Id, "user.email_verified");
        return DonePage(slug, instance, "Email verified", "Your email address has been verified.");
    }
}

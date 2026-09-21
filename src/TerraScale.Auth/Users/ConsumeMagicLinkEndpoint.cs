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

internal sealed class ConsumeMagicLinkEndpoint : IEndpoint
{
    public RouteHandlerBuilder Map(IEndpointRouteBuilder routes) =>
        routes.MapGet("/ui/{slug}/magic-link/consume", HandleAsync);

    private static async Task<IResult> HandleAsync(string slug, HttpRequest request, AuthStore store, SessionService sessions, CancellationToken ct)
    {
        var instance = await PageInstanceAsync(store, slug, ct);
        if (instance is null)
            return NotFoundPage();

        var record = await ConsumeTokenAsync(store, instance.Id, request.Query["token"].ToString(), "magic_link", ct);
        var user = record?.UserId is { } userId ? await store.GetUserAsync(userId, ct) : null;
        if (user is null || user.Deleted || user.Banned)
            return InvalidLinkPage(slug, instance);

        await EstablishSessionAsync(store, sessions, request.HttpContext, instance, user, "magic_link", ct);
        return Results.Redirect($"/ui/{slug}/account");
    }
}

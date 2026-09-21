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

internal sealed class GetAccountEndpoint : IEndpoint
{
    public RouteHandlerBuilder Map(IEndpointRouteBuilder routes) =>
        routes.MapGet("/ui/{slug}/account", HandleAsync);

    private static async Task<IResult> HandleAsync(string slug, HttpRequest request, AuthStore store, SessionService sessions, CancellationToken ct)
    {
        var instance = await PageInstanceAsync(store, slug, ct);
        if (instance is null)
            return NotFoundPage();

        if (await ResolvePageSessionAsync(request, sessions, instance.Id, ct) is not { } current)
            return RedirectToSignIn(slug, $"/ui/{slug}/account");
        return AccountPage(slug, current.User, current.Session.Id);
    }
}

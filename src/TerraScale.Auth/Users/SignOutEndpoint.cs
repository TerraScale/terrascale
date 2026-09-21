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

internal sealed class SignOutEndpoint : IEndpoint
{
    public RouteHandlerBuilder Map(IEndpointRouteBuilder routes) =>
        routes.MapPost("/ui/{slug}/sign-out", HandleAsync);

    private static async Task<IResult> HandleAsync(string slug, HttpRequest request, AuthStore store, SessionService sessions, CancellationToken ct)
    {
        var instance = await PageInstanceAsync(store, slug, ct);
        if (instance is null)
            return NotFoundPage();

        if (request.Cookies[SessionCookies.Name] is { Length: > 0 } token
            && await sessions.ResolveAsync(instance.Id, token, ct) is { } pair)
        {
            await sessions.RevokeAsync(instance.Id, pair.Session.Id, ct);
            await AuditAsync(store, instance.Id, pair.User.Id, "user.signed_out");
        }
        request.HttpContext.Response.Cookies.Delete(SessionCookies.Name, SessionCookies.Options);
        return Results.Redirect($"/ui/{slug}/sign-in");
    }
}

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

internal sealed class RevokeSessionEndpoint : IEndpoint
{
    public RouteHandlerBuilder Map(IEndpointRouteBuilder routes) =>
        routes.MapPost("/ui/{slug}/account/sessions/{id}/revoke", HandleAsync);

    private static async Task<IResult> HandleAsync(string slug, string id, HttpRequest request, AuthStore store, SessionService sessions, CancellationToken ct)
    {
        var instance = await PageInstanceAsync(store, slug, ct);
        if (instance is null)
            return NotFoundPage();

        if (await ResolvePageSessionAsync(request, sessions, instance.Id, ct) is not { } current)
            return RedirectToSignIn(slug, $"/ui/{slug}/account/sessions");

        var target = await store.GetSessionAsync(id, ct);
        if (target is not null && target.InstanceId == instance.Id && target.UserId == current.User.Id && target.Status == "active")
        {
            await store.UpsertSessionAsync(target with { Status = "revoked" }, ct);
            if (target.Id == current.Session.Id)
                request.HttpContext.Response.Cookies.Delete(SessionCookies.Name, SessionCookies.Options);
        }
        return Results.Redirect($"/ui/{slug}/account/sessions");
    }
}

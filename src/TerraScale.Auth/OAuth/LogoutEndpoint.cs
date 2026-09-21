namespace TerraScale.Auth.OAuth;

using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TerraScale.Auth.Api;
using TerraScale.Auth.Configuration;
using TerraScale.Auth.Crypto;
using TerraScale.Auth.Keys;
using TerraScale.Auth.Store;
using TerraScale.Auth.Ui;
using TerraScale.DotNet;
using TerraScale.Endpoints;
using static TerraScale.Auth.OAuth.OAuthSupport;

internal sealed class LogoutEndpoint : IEndpoint
{
    public RouteHandlerBuilder Map(IEndpointRouteBuilder routes) =>
        routes.MapGet("/{slug}/oauth/logout", HandleAsync);

    private static async Task<IResult> HandleAsync(string slug, HttpRequest request, AuthStore store,
            Users.SessionService sessions, CancellationToken ct)
    {
        if (await store.FindInstanceBySlugAsync(slug, ct) is not { } instance)
            return ApiResults.NotFound();

        // Kill the hosted-UI session behind the cookie, then clear the cookie.
        // id_token_hint is accepted but not load-bearing in v1 (the session
        // cookie is the authority on who is signed in).
        var cookie = request.Cookies[SessionCookies.Name];
        if (cookie is not null && await sessions.ResolveAsync(instance.Id, cookie, ct) is { } resolved)
            await sessions.RevokeAsync(instance.Id, resolved.Session.Id, ct);
        request.HttpContext.Response.Cookies.Delete(SessionCookies.Name, SessionCookies.Options);

        var query = request.Query;
        var clientId = query["client_id"].ToString();
        var postLogoutUri = query["post_logout_redirect_uri"].ToString();
        var state = query["state"].ToString();
        if (postLogoutUri.Length > 0 && clientId.Length > 0
            && await store.FindOAuthClientAsync(instance.Id, clientId, ct) is { } client
            && client.RedirectUris.Any(registered => SharesOrigin(registered, postLogoutUri)))
        {
            var target = state.Length > 0 ? AppendQuery(postLogoutUri, "state=" + Uri.EscapeDataString(state)) : postLogoutUri;
            return Results.Redirect(target);
        }
        return Ui.Page("Signed out", """
            <h1>Signed out</h1>
            <p class="muted">You have been signed out.</p>
            """);
    }

    private static bool SharesOrigin(string registered, string candidate) =>
        Uri.TryCreate(registered, UriKind.Absolute, out var a)
        && Uri.TryCreate(candidate, UriKind.Absolute, out var b)
        && a.Scheme == b.Scheme && a.Host == b.Host && a.Port == b.Port;
}

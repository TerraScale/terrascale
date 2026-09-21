namespace TerraScale.Auth.Connections;

using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using TerraScale.Auth.Configuration;
using TerraScale.Auth.Crypto;
using TerraScale.Auth.Keys;
using TerraScale.Auth.Store;
using TerraScale.Auth.Users;
using TerraScale.Endpoints;
using static TerraScale.Auth.Connections.ConnectionFlow;

using static TerraScale.Auth.Connections.OidcLogin;

internal sealed class AuthorizeOidcEndpoint : IEndpoint
{
    public RouteHandlerBuilder Map(IEndpointRouteBuilder routes) =>
        routes.MapGet("/ui/{slug}/connections/{connectionId}/oidc/authorize", HandleAsync);

    private static async Task<IResult> HandleAsync(string slug, string connectionId, AuthOptions options, AuthStore store, CancellationToken ct)
    {
        var instance = await store.FindInstanceBySlugAsync(slug, ct);
        if (instance is null)
            return ConnectionFlow.NotFoundPage();
        var connection = await ConnectionFlow.FindConnectionAsync(store, instance.Id, connectionId, "oidc", ct);
        if (connection is null)
            return ConnectionFlow.NotFoundPage();

        var state = await ConnectionFlow.MintStateAsync(store, instance.Id, connection.Id, "oidc.state", requestId: null, ConnectionFlow.OidcStateLifetime, ct);
        var separator = connection.AuthorizationEndpoint!.Contains('?') ? '&' : '?';
        return Results.Redirect(
            connection.AuthorizationEndpoint
            + separator
            + Query(new Dictionary<string, string>
            {
                ["client_id"] = connection.ClientId!,
                ["redirect_uri"] = CallbackUrl(options, slug, connection.Id),
                ["response_type"] = "code",
                ["scope"] = "openid email profile",
                ["state"] = state,
            }));
    }
}

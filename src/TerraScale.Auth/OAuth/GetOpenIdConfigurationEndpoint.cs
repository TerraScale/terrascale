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

internal sealed class GetOpenIdConfigurationEndpoint : IEndpoint
{
    public RouteHandlerBuilder Map(IEndpointRouteBuilder routes) =>
        routes.MapGet("/{slug}/.well-known/openid-configuration", HandleAsync);

    private static async Task<IResult> HandleAsync(string slug, AuthStore store, AuthOptions options, CancellationToken ct)
    {
        if (await store.FindInstanceBySlugAsync(slug, ct) is not { } instance)
            return ApiResults.NotFound();
        var issuer = Issuer(options, instance.Slug);
        return Results.Json(new Dictionary<string, object>
        {
            ["issuer"] = issuer,
            ["authorization_endpoint"] = $"{issuer}/oauth/authorize",
            ["token_endpoint"] = $"{issuer}/oauth/token",
            ["userinfo_endpoint"] = $"{issuer}/oauth/userinfo",
            ["jwks_uri"] = $"{issuer}/.well-known/jwks.json",
            ["revocation_endpoint"] = $"{issuer}/oauth/revoke",
            ["introspection_endpoint"] = $"{issuer}/oauth/introspect",
            ["end_session_endpoint"] = $"{issuer}/oauth/logout",
            ["response_types_supported"] = new[] { "code" },
            ["grant_types_supported"] = new[] { "authorization_code", "refresh_token", "client_credentials" },
            ["code_challenge_methods_supported"] = new[] { "S256" },
            ["subject_types_supported"] = new[] { "public" },
            ["id_token_signing_alg_values_supported"] = new[] { "ES256" },
            ["token_endpoint_auth_methods_supported"] = new[] { "client_secret_basic", "client_secret_post", "none" },
            ["scopes_supported"] = SupportedScopes,
        });
    }
}

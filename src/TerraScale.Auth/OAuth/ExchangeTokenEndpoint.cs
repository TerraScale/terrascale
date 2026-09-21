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

internal sealed class ExchangeTokenEndpoint : IEndpoint
{
    public RouteHandlerBuilder Map(IEndpointRouteBuilder routes) =>
        routes.MapPost("/{slug}/oauth/token", HandleAsync);

    private static async Task<IResult> HandleAsync(string slug, HttpRequest request, AuthStore store, KeyRing keys,
            AuthOptions options, CancellationToken ct)
    {
        if (await store.FindInstanceBySlugAsync(slug, ct) is not { } instance)
            return ApiResults.NotFound();
        var form = await request.ReadFormAsync(ct);

        var authentication = await AuthenticateClientAsync(request, form, store, instance.Id, ct);
        if (authentication.Client is null)
            return TokenError("invalid_client", authentication.Description ?? "client authentication failed", authentication.Status);

        var issuer = Issuer(options, instance.Slug);
        var requestedGrant = form["grant_type"].ToString();
        if (requestedGrant.Length == 0)
            return TokenError("invalid_request", "grant_type is required");
        return requestedGrant switch
        {
            "authorization_code" => await AuthorizationCodeGrantAsync(store, keys, issuer, instance, authentication.Client, form, ct),
            "refresh_token" => await RefreshTokenGrantAsync(store, keys, issuer, instance, authentication.Client, form, ct),
            "client_credentials" => await ClientCredentialsGrantAsync(keys, issuer, instance, authentication.Client, form, ct),
            var grantType => TokenError("unsupported_grant_type", $"grant_type '{grantType}' is not supported"),
        };
    }

    private static async Task<IResult> RefreshTokenGrantAsync(AuthStore store, KeyRing keys, string issuer,
        Instance instance, OAuthClient client, IFormCollection form, CancellationToken ct)
    {
        var tokenValue = form["refresh_token"].ToString();
        if (tokenValue.Length == 0)
            return TokenError("invalid_request", "refresh_token is required");

        var token = await store.FindRefreshTokenAsync(SecureTokens.Sha256Hex(tokenValue), ct);
        if (token is null || token.InstanceId != instance.Id)
            return TokenError("invalid_grant", "unknown refresh token");
        if (token.ClientId != client.ClientId)
            return TokenError("invalid_grant", "refresh token was issued to another client");
        if (token.Status != "active" || token.ExpiresAt <= DateTimeOffset.UtcNow.ToUnixTimeSeconds())
        {
            // Reuse of a rotated/revoked token: assume theft and kill the family.
            await RevokeRefreshFamilyAsync(store, instance.Id, client.ClientId, token.UserId, ct);
            return TokenError("invalid_grant", "refresh token is no longer active");
        }
        if (await store.GetUserAsync(token.UserId, ct) is not { Banned: false, Deleted: false })
        {
            // A banned/deleted user must not keep minting tokens; kill the family.
            await RevokeRefreshFamilyAsync(store, instance.Id, client.ClientId, token.UserId, ct);
            return TokenError("invalid_grant", "user is no longer active");
        }

        var (refreshToken, refreshId) = await CreateRefreshTokenAsync(store, instance.Id, client.ClientId, token.UserId, token.Scopes, ct);
        await store.UpsertRefreshTokenAsync(token with { Status = "rotated", RotatedTo = refreshId }, ct);
        await AuditAsync(store, instance.Id, "oauth.token_refreshed", token.UserId, new Dictionary<string, object> { ["client_id"] = client.ClientId }, ct);

        var scope = string.Join(' ', token.Scopes);
        var accessToken = await SignAccessTokenAsync(keys, instance.Id, issuer, token.UserId, client.ClientId, scope, null, ct);
        return Results.Json(new Dictionary<string, object>
        {
            ["access_token"] = accessToken,
            ["token_type"] = "Bearer",
            ["expires_in"] = AccessTokenLifetimeSeconds,
            ["scope"] = scope,
            ["refresh_token"] = refreshToken,
        });
    }

    private static async Task<IResult> ClientCredentialsGrantAsync(KeyRing keys, string issuer,
        Instance instance, OAuthClient client, IFormCollection form, CancellationToken ct)
    {
        if (client.Public || client.ClientSecretHash is null)
            return TokenError("unauthorized_client", "client_credentials requires an authenticated confidential client");

        var scope = form["scope"].ToString();
        var accessToken = await SignAccessTokenAsync(keys, instance.Id, issuer, client.ClientId, client.ClientId, scope, null, ct);
        return Results.Json(new Dictionary<string, object>
        {
            ["access_token"] = accessToken,
            ["token_type"] = "Bearer",
            ["expires_in"] = AccessTokenLifetimeSeconds,
            ["scope"] = scope,
        });
    }
}

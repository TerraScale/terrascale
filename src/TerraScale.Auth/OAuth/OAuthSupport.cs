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

internal static class OAuthSupport
{
    internal static readonly string[] SupportedScopes = ["openid", "profile", "email"];

    internal const int AccessTokenLifetimeSeconds = 3600;

    internal static readonly TimeSpan RefreshTokenLifetime = TimeSpan.FromDays(30);

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Management request body for OAuth client creation.</summary>
    public sealed record CreateOAuthClientRequest(
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("redirect_uris")] string[]? RedirectUris,
        [property: JsonPropertyName("public")] bool Public,
        [property: JsonPropertyName("token_endpoint_auth_method")] string? TokenEndpointAuthMethod);

    // ---- Token grants ---------------------------------------------------------------

    internal static async Task<IResult> AuthorizationCodeGrantAsync(AuthStore store, KeyRing keys, string issuer,
        Instance instance, OAuthClient client, IFormCollection form, CancellationToken ct)
    {
        var code = form["code"].ToString();
        if (code.Length == 0)
            return TokenError("invalid_request", "code is required");

        var record = await store.FindAuthorizationCodeAsync(SecureTokens.Sha256Hex(code), ct);
        if (record is null || record.InstanceId != instance.Id || record.ClientId != client.ClientId)
            return TokenError("invalid_grant", "invalid authorization code");
        if (record.Consumed)
        {
            // RFC 6749 4.1.2: replayed code means theft — revoke the tokens it
            // produced (the refresh family for this user+client).
            await RevokeRefreshFamilyAsync(store, instance.Id, client.ClientId, record.UserId, ct);
            return TokenError("invalid_grant", "authorization code already used");
        }
        if (record.ExpiresAt <= DateTimeOffset.UtcNow.ToUnixTimeSeconds())
            return TokenError("invalid_grant", "authorization code expired");
        if (form["redirect_uri"].ToString() != record.RedirectUri)
            return TokenError("invalid_grant", "redirect_uri does not match the authorization request");

        if (record.CodeChallenge is null)
        {
            if (client.Public)
                return TokenError("invalid_grant", "PKCE is required for public clients");
        }
        else
        {
            var verifier = form["code_verifier"].ToString();
            if (verifier.Length == 0)
                return TokenError("invalid_grant", "code_verifier is required");
            var derived = Encodings.Base64UrlEncode(System.Security.Cryptography.SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
            if (!SecureTokens.FixedTimeEquals(derived, record.CodeChallenge))
                return TokenError("invalid_grant", "code_verifier does not match the code challenge");
        }

        // Consume before issuing so a concurrent replay cannot win (single-use codes).
        await store.UpsertAuthorizationCodeAsync(record with { Consumed = true }, ct);

        if (await store.GetUserAsync(record.UserId, ct) is not { } user)
            return TokenError("invalid_grant", "user not found");
        var sessionId = record.Id.Split(':')[1];
        var session = await store.GetSessionAsync(sessionId, ct);
        var scope = string.Join(' ', record.Scopes);

        var accessToken = await SignAccessTokenAsync(keys, instance.Id, issuer, user.Id, client.ClientId, scope, session?.Id, ct);
        var (refreshToken, refreshId) = await CreateRefreshTokenAsync(store, instance.Id, client.ClientId, user.Id, record.Scopes, ct);

        var response = new Dictionary<string, object>
        {
            ["access_token"] = accessToken,
            ["token_type"] = "Bearer",
            ["expires_in"] = AccessTokenLifetimeSeconds,
            ["scope"] = scope,
            ["refresh_token"] = refreshToken,
        };
        if (record.Scopes.Contains("openid"))
        {
            var idTokenClaims = new Dictionary<string, object>
            {
                ["iss"] = issuer,
                ["sub"] = user.Id,
                ["aud"] = client.ClientId,
                ["at_hash"] = Encodings.Base64UrlEncode(System.Security.Cryptography.SHA256.HashData(Encoding.ASCII.GetBytes(accessToken))[..16]),
            };
            if (session is not null)
                idTokenClaims["auth_time"] = session.CreatedAt;
            if (record.Nonce is not null)
                idTokenClaims["nonce"] = record.Nonce;
            if (record.Scopes.Contains("email") && user.Email is not null)
            {
                idTokenClaims["email"] = user.Email;
                idTokenClaims["email_verified"] = user.EmailVerified;
            }
            var key = await keys.GetCurrentAsync(instance.Id, ct);
            response["id_token"] = JsonWebToken.Sign(idTokenClaims, DateTimeOffset.UtcNow.AddSeconds(AccessTokenLifetimeSeconds), key);
        }
        return Results.Json(response);
    }

    // ---- Shared plumbing ----------------------------------------------------------------

    internal sealed record ClientAuthentication(OAuthClient? Client, string? Description, int Status = 401);

    /// <summary>Client authentication per RFC 6749 2.3.1: Basic (client_secret_basic) or form (client_secret_post); public clients send client_id alone.</summary>
    internal static async Task<ClientAuthentication> AuthenticateClientAsync(HttpRequest request, IFormCollection form,
        AuthStore store, string instanceId, CancellationToken ct)
    {
        string clientId;
        string? secret = null;
        var header = request.Headers.Authorization.ToString();
        var usedBasic = header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase);

        // RFC 6749 5.2: a 401 for a client that tried the Authorization header
        // must carry WWW-Authenticate naming that scheme.
        ClientAuthentication Fail(string description)
        {
            if (usedBasic)
                request.HttpContext.Response.Headers.WWWAuthenticate = "Basic";
            return new(null, description);
        }

        if (usedBasic)
        {
            try
            {
                var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(header["Basic ".Length..].Trim()));
                var separator = decoded.IndexOf(':');
                if (separator < 0)
                    return Fail("malformed Basic authorization header");
                clientId = Uri.UnescapeDataString(decoded[..separator]);
                secret = Uri.UnescapeDataString(decoded[(separator + 1)..]);
            }
            catch (FormatException)
            {
                return Fail("malformed Basic authorization header");
            }
        }
        else
        {
            clientId = form["client_id"].ToString();
            var formSecret = form["client_secret"].ToString();
            secret = formSecret.Length == 0 ? null : formSecret;
        }

        if (clientId.Length == 0)
            return Fail("client_id is required");
        if (await store.FindOAuthClientAsync(instanceId, clientId, ct) is not { } client)
            return Fail("unknown client");
        if (client.ClientSecretHash is null)
            return new(client, null); // public client: nothing to verify
        if (secret is null)
            return Fail("client secret required");
        return SecureTokens.FixedTimeEquals(SecureTokens.Sha256Hex(secret), client.ClientSecretHash)
            ? new(client, null)
            : Fail("invalid client secret");
    }

    /// <summary>Revokes every active refresh token for a user+client pair (the token "family").</summary>
    internal static async Task RevokeRefreshFamilyAsync(AuthStore store, string instanceId, string clientId, string userId, CancellationToken ct)
    {
        foreach (var sibling in await store.ListRefreshTokensByUserAsync(userId, ct))
            if (sibling.InstanceId == instanceId && sibling.ClientId == clientId && sibling.Status == "active")
                await store.UpsertRefreshTokenAsync(sibling with { Status = "revoked" }, ct);
    }

    internal static async Task<string> SignAccessTokenAsync(KeyRing keys, string instanceId, string issuer,
        string subject, string audience, string scope, string? sessionId, CancellationToken ct)
    {
        var claims = new Dictionary<string, object>
        {
            ["iss"] = issuer,
            ["sub"] = subject,
            ["aud"] = audience,
            ["scope"] = scope,
        };
        if (sessionId is not null)
            claims["sid"] = sessionId;
        var key = await keys.GetCurrentAsync(instanceId, ct);
        return JsonWebToken.Sign(claims, DateTimeOffset.UtcNow.AddSeconds(AccessTokenLifetimeSeconds), key);
    }

    internal static async Task<(string Token, string Id)> CreateRefreshTokenAsync(AuthStore store, string instanceId,
        string clientId, string userId, string[] scopes, CancellationToken ct)
    {
        var token = SecureTokens.RandomToken(48);
        var now = DateTimeOffset.UtcNow;
        var id = "rtk_" + SecureTokens.RandomToken(20);
        await store.CreateRefreshTokenAsync(new RefreshToken
        {
            Id = id,
            InstanceId = instanceId,
            ClientId = clientId,
            UserId = userId,
            TokenKey = SecureTokens.Sha256Hex(token),
            Status = "active",
            Scopes = scopes,
            CreatedAt = now.ToUnixTimeSeconds(),
            ExpiresAt = now.Add(RefreshTokenLifetime).ToUnixTimeSeconds(),
        }, ct);
        return (token, id);
    }

    internal static async Task<Dictionary<string, object>?> VerifyAccessTokenAsync(KeyRing keys, AuthStore store,
        AuthOptions options, Instance instance, string token, CancellationToken ct)
    {
        var activeKeys = await keys.GetActiveAsync(instance.Id, ct);
        var claims = JsonWebToken.Verify(token, kid => (SigningKey?)activeKeys.FirstOrDefault(key => key.Id == kid));
        if (claims is null)
            return null;
        if (ClaimString(claims, "iss") != Issuer(options, instance.Slug))
            return null;
        // The audience must be a client of this instance (exp is checked by Verify).
        return ClaimString(claims, "aud") is { } audience
            && await store.FindOAuthClientAsync(instance.Id, audience, ct) is not null
            ? claims
            : null;
    }

    /// <summary>The safe client shape returned by the management API (never the secret hash).</summary>
    internal static object ClientView(OAuthClient client) => new
    {
        client.Id,
        client.ClientId,
        client.Name,
        client.RedirectUris,
        client.TokenEndpointAuthMethod,
        client.Public,
        client.CreatedAt,
    };

    internal static string? ClaimString(Dictionary<string, object> claims, string name) =>
        claims.TryGetValue(name, out var value)
            && value is JsonElement { ValueKind: JsonValueKind.String } element
                ? element.GetString()
                : null;

    internal static string Issuer(AuthOptions options, string slug) =>
        options.PublicOrigin.ToString().TrimEnd('/') + "/" + slug;

    internal static string AppendQuery(string uri, string query) =>
        uri.Contains('?') ? $"{uri}&{query}" : $"{uri}?{query}";

    internal static IResult TokenError(string error, string description, int status = 400) =>
        Results.Json(new { error, error_description = description }, statusCode: status);

    internal static Task AuditAsync(AuthStore store, string instanceId, string type, string? actorId, Dictionary<string, object> data, CancellationToken ct) =>
        store.CreateAuditEventAsync(new AuditEvent
        {
            Id = "evt_" + SecureTokens.RandomToken(20),
            InstanceId = instanceId,
            Type = type,
            ActorId = actorId,
            Data = data,
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        }, ct);
}

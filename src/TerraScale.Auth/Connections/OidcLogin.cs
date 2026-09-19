namespace TerraScale.Auth.Connections;

using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using TerraScale.Auth.Configuration;
using TerraScale.Auth.Crypto;
using TerraScale.Auth.Keys;
using TerraScale.Auth.Store;
using TerraScale.Auth.Users;

/// <summary>Social/OIDC sign-in: the authorization code flow from scratch.</summary>
internal static class OidcLogin
{
    private static readonly TimeSpan JwksTtl = TimeSpan.FromHours(1);

    /// <summary>Upstream signing keys per connection, refreshed hourly.</summary>
    private static readonly ConcurrentDictionary<string, (DateTimeOffset FetchedAt, List<SigningKey> Keys)> JwksCache = new();

    internal static async Task<IResult> AuthorizeAsync(string slug, string connectionId, AuthOptions options, AuthStore store, CancellationToken ct)
    {
        var instance = await store.FindInstanceBySlugAsync(slug, ct);
        if (instance is null)
            return ConnectionEndpoints.NotFoundPage();
        var connection = await ConnectionEndpoints.FindConnectionAsync(store, instance.Id, connectionId, "oidc", ct);
        if (connection is null)
            return ConnectionEndpoints.NotFoundPage();

        var state = await ConnectionEndpoints.MintStateAsync(store, instance.Id, connection.Id, "oidc.state", requestId: null, ConnectionEndpoints.OidcStateLifetime, ct);
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

    internal static async Task<IResult> CallbackAsync(
        string slug, string connectionId, HttpRequest request, AuthOptions options, AuthStore store, SessionService sessions, CancellationToken ct)
    {
        var instance = await store.FindInstanceBySlugAsync(slug, ct);
        if (instance is null)
            return ConnectionEndpoints.NotFoundPage();

        // State first: single-use, unexpired, minted for this connection. Consumed even if the
        // rest of the flow fails, so a leaked browser URL cannot be replayed.
        var state = request.Query["state"].ToString();
        if (await ConnectionEndpoints.ConsumeStateAsync(store, instance, connectionId, state, "oidc.state", ct) is null)
            return ConnectionEndpoints.ErrorPage(slug, "This sign-in attempt is invalid, expired, or already used.");
        var connection = await ConnectionEndpoints.FindConnectionAsync(store, instance.Id, connectionId, "oidc", ct);
        if (connection is null)
            return ConnectionEndpoints.NotFoundPage();

        var code = request.Query["code"].ToString();
        if (code.Length == 0)
            return ConnectionEndpoints.ErrorPage(slug, "The identity provider did not return an authorization code.");

        try
        {
            return await CompleteSignInAsync(slug, instance, connection, code, options, store, sessions, request.HttpContext, ct);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException or CryptographicException)
        {
            return ConnectionEndpoints.ErrorPage(slug, "Contacting the identity provider failed.", 502);
        }
    }

    private static async Task<IResult> CompleteSignInAsync(
        string slug, Instance instance, Connection connection, string code, AuthOptions options, AuthStore store, SessionService sessions, HttpContext httpContext, CancellationToken ct)
    {
        var secret = Encoding.UTF8.GetString(SecretBox.Decrypt(options.MasterKey, connection.ClientSecretBox!));
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = CallbackUrl(options, slug, connection.Id),
            ["client_id"] = connection.ClientId!,
            ["client_secret"] = secret, // client_secret_post
        });
        using var tokenResponse = await ConnectionEndpoints.Upstream.PostAsync(connection.TokenEndpoint!, content, ct);
        if (!tokenResponse.IsSuccessStatusCode)
            return ConnectionEndpoints.ErrorPage(slug, "Exchanging the authorization code failed.", 502);
        using var tokens = JsonDocument.Parse(await tokenResponse.Content.ReadAsStringAsync(ct));
        if (!tokens.RootElement.TryGetProperty("id_token", out var idTokenElement) || idTokenElement.GetString() is not { Length: > 0 } idToken)
            return ConnectionEndpoints.ErrorPage(slug, "The identity provider did not return an id_token.", 502);

        var keys = await GetSigningKeysAsync(connection, ct);
        var claims = keys is null ? null : JsonWebToken.Verify(idToken, kid => ResolveKey(keys, kid));
        if (claims is null)
        {
            // The cached JWKS may be stale after an upstream key rotation: force one refresh and retry.
            if ((keys = await GetSigningKeysAsync(connection, ct, forceRefresh: true)) is not null)
                claims = JsonWebToken.Verify(idToken, kid => ResolveKey(keys, kid));
        }
        if (claims is null)
            return ConnectionEndpoints.ErrorPage(slug, "The id_token signature could not be verified.");
        if (!string.Equals(ClaimString(claims, "iss"), connection.IssuerUrl, StringComparison.OrdinalIgnoreCase))
            return ConnectionEndpoints.ErrorPage(slug, "The id_token issuer does not match the connection.");
        if (!ClaimStrings(claims, "aud").Contains(connection.ClientId!, StringComparer.Ordinal))
            return ConnectionEndpoints.ErrorPage(slug, "The id_token audience does not match this connection.");

        // Email comes from the id_token; fall back to the userinfo endpoint when the claim is absent.
        var email = ClaimString(claims, "email");
        var emailVerified = ClaimBool(claims, "email_verified");
        var name = ClaimString(claims, "name");
        if (email is null)
        {
            if (connection.UserinfoEndpoint is null
                || !tokens.RootElement.TryGetProperty("access_token", out var accessTokenElement)
                || accessTokenElement.GetString() is not { Length: > 0 } accessToken)
                return ConnectionEndpoints.ErrorPage(slug, "The identity provider did not provide an email address.");
            using var userinfoRequest = new HttpRequestMessage(HttpMethod.Get, connection.UserinfoEndpoint);
            userinfoRequest.Headers.Authorization = new("Bearer", accessTokenElement.GetString());
            using var userinfoResponse = await ConnectionEndpoints.Upstream.SendAsync(userinfoRequest, ct);
            if (!userinfoResponse.IsSuccessStatusCode)
                return ConnectionEndpoints.ErrorPage(slug, "Fetching the user profile failed.", 502);
            using var userinfo = JsonDocument.Parse(await userinfoResponse.Content.ReadAsStringAsync(ct));
            email = StringProperty(userinfo.RootElement, "email");
            emailVerified ??= BoolProperty(userinfo.RootElement, "email_verified");
            name ??= StringProperty(userinfo.RootElement, "name");
        }

        if (email is null)
            return ConnectionEndpoints.ErrorPage(slug, "The identity provider did not provide an email address.");
        // An unconfirmed upstream email must never link to (or create) a local account: a lax or
        // attacker-controlled provider could otherwise assert a victim's address and take it over.
        if (emailVerified is not true)
            return ConnectionEndpoints.ErrorPage(slug, "The identity provider did not confirm this email address.");
        var user = await ConnectionEndpoints.FindOrCreateUserAsync(store, sessions, instance, email, emailVerified.Value, name, ct);
        if (user is null)
            return ConnectionEndpoints.ErrorPage(slug, "Could not create or access an account for this identity.");

        await ConnectionEndpoints.SignInAsync(store, sessions, httpContext, instance, user, $"oidc:{connection.Id}", ct);
        return Results.Redirect($"/ui/{slug}/account");
    }

    // ---- Upstream metadata and keys -------------------------------------------------------

    internal sealed record OidcDiscovery(string? Issuer, string? AuthorizationEndpoint, string? TokenEndpoint, string? UserinfoEndpoint, string? JwksUri);

    /// <summary>Fetches {issuer}/.well-known/openid-configuration; null on any transport or shape failure.</summary>
    internal static async Task<OidcDiscovery?> FetchDiscoveryAsync(Uri issuer, CancellationToken ct)
    {
        try
        {
            using var response = await ConnectionEndpoints.Upstream.GetAsync(issuer.ToString().TrimEnd('/') + "/.well-known/openid-configuration", ct);
            if (!response.IsSuccessStatusCode)
                return null;
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            return new OidcDiscovery(
                StringProperty(document.RootElement, "issuer"),
                StringProperty(document.RootElement, "authorization_endpoint"),
                StringProperty(document.RootElement, "token_endpoint"),
                StringProperty(document.RootElement, "userinfo_endpoint"),
                StringProperty(document.RootElement, "jwks_uri"));
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or InvalidOperationException or JsonException)
        {
            return null;
        }
    }

    /// <summary>Cached JWKS for the connection: discovery gives the jwks_uri, the JWKs become signing keys.</summary>
    private static async Task<List<SigningKey>?> GetSigningKeysAsync(Connection connection, CancellationToken ct, bool forceRefresh = false)
    {
        if (!forceRefresh && JwksCache.TryGetValue(connection.Id, out var cached) && cached.FetchedAt + JwksTtl > DateTimeOffset.UtcNow)
            return cached.Keys;

        if ((await FetchDiscoveryAsync(new Uri(connection.IssuerUrl!), ct))?.JwksUri is not { } jwksUri)
            return null;
        try
        {
            using var response = await ConnectionEndpoints.Upstream.GetAsync(jwksUri, ct);
            if (!response.IsSuccessStatusCode)
                return null;
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            if (!document.RootElement.TryGetProperty("keys", out var jwks) || jwks.ValueKind != JsonValueKind.Array)
                return null;

            var keys = new List<SigningKey>();
            for (var index = 0; index < jwks.GetArrayLength(); index++)
            {
                if (BuildKey(jwks[index], index) is { } key)
                    keys.Add(key);
            }
            JwksCache[connection.Id] = (DateTimeOffset.UtcNow, keys);
            return keys;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or InvalidOperationException or JsonException or FormatException or CryptographicException or ArgumentException)
        {
            return null;
        }
    }

    private static SigningKey? BuildKey(JsonElement jwk, int index)
    {
        var kid = StringProperty(jwk, "kid") ?? "jwk-" + index;
        var kty = StringProperty(jwk, "kty");
        if (kty == "RSA"
            && StringProperty(jwk, "n") is { } modulus
            && StringProperty(jwk, "e") is { } exponent)
        {
            return RsaSigningKey.FromModulusExponent(Encodings.Base64UrlDecode(modulus), Encodings.Base64UrlDecode(exponent), kid);
        }
        if (kty == "EC"
            && StringProperty(jwk, "crv") == "P-256"
            && StringProperty(jwk, "x") is { } x
            && StringProperty(jwk, "y") is { } y)
        {
            return EcdsaPublicSigningKey.FromCoordinates(Encodings.Base64UrlDecode(x), Encodings.Base64UrlDecode(y), kid);
        }
        return null; // oct/other key types cannot protect an upstream id_token
    }

    /// <summary>Exact kid match; a single unnamed key still resolves for kid-less headers.</summary>
    private static SigningKey? ResolveKey(List<SigningKey> keys, string kid) =>
        keys.FirstOrDefault(key => key.Id == kid) ?? (keys.Count == 1 ? keys[0] : null);

    private static string CallbackUrl(AuthOptions options, string slug, string connectionId) =>
        $"{options.PublicOrigin.ToString().TrimEnd('/')}/{slug}/connections/{connectionId}/oidc/callback";

    private static string Query(Dictionary<string, string> parameters) =>
        string.Join("&", parameters.Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));

    private static string? ClaimString(Dictionary<string, object> claims, string name) =>
        claims.TryGetValue(name, out var value) && value is JsonElement { ValueKind: JsonValueKind.String } element
            ? element.GetString()
            : null;

    private static bool? ClaimBool(Dictionary<string, object> claims, string name) =>
        claims.TryGetValue(name, out var value) && value is JsonElement element
            ? element.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.String => bool.TryParse(element.GetString(), out var parsed) ? parsed : null,
                _ => null,
            }
            : null;

    private static List<string> ClaimStrings(Dictionary<string, object> claims, string name)
    {
        if (!claims.TryGetValue(name, out var value) || value is not JsonElement element)
            return [];
        return element.ValueKind switch
        {
            JsonValueKind.String => [element.GetString() ?? ""],
            JsonValueKind.Array => element.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString() ?? "")
                .ToList(),
            _ => [],
        };
    }

    private static string? StringProperty(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var property)
        && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static bool? BoolProperty(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var property)
            ? property.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => null,
            }
            : null;
}

/// <summary>Verify-only ES256 key built from public JWK coordinates (the Crypto EcdsaSigningKey import path requires private material).</summary>
internal sealed class EcdsaPublicSigningKey : SigningKey
{
    private readonly ECDsa ecdsa = ECDsa.Create();

    public override string Algorithm => "ES256";

    public static EcdsaPublicSigningKey FromCoordinates(byte[] x, byte[] y, string id)
    {
        var key = new EcdsaPublicSigningKey { Id = id };
        key.ecdsa.ImportParameters(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = x, Y = y },
        });
        return key;
    }

    public override byte[] Sign(ReadOnlySpan<byte> input) => throw new NotSupportedException("verify-only key");

    public override bool Verify(ReadOnlySpan<byte> input, ReadOnlySpan<byte> signature) =>
        signature.Length == 64
        && ecdsa.VerifyData(input.ToArray(), signature.ToArray(), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

    public override Dictionary<string, object> ToPublicJwk() => new()
    {
        ["kty"] = "EC",
        ["crv"] = "P-256",
        ["use"] = "sig",
        ["alg"] = "ES256",
        ["kid"] = Id,
        ["x"] = Encodings.Base64UrlEncode(ecdsa.ExportParameters(false).Q.X!),
        ["y"] = Encodings.Base64UrlEncode(ecdsa.ExportParameters(false).Q.Y!),
    };
}

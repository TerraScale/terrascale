using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TerraScale.DotNet;

/// <summary>A backend-asserted, rule-bound end-user token; no refresh token or configurable lifetime.</summary>
/// <param name="AccessToken">Opaque compact access token, at most 8192 ASCII bytes. Keep out of logs.</param>
/// <param name="TokenType">Bearer.</param>
/// <param name="ExpiresIn">Remaining seconds, 1–900, capped at the minting key's expiry.</param>
public sealed record TerraScaleAuthEndUserToken(string AccessToken, string TokenType, long ExpiresIn)
{
    /// <summary>The sensitive access token.</summary>
    [JsonPropertyName("access_token")]
    public string AccessToken { get; init; } = AccessToken is { Length: > 0 and <= 8192 }
        && AccessToken.All(c => c is > ' ' and < '\x7f') ? AccessToken : throw new JsonException("Invalid end-user access token.");

    /// <summary>The Bearer authorization scheme.</summary>
    [JsonPropertyName("token_type")]
    public string TokenType { get; init; } = TokenType == "Bearer" ? TokenType : throw new JsonException("Invalid token type.");

    /// <summary>Remaining lifetime in seconds; issued tokens validate offline until expiry plus server clock skew.</summary>
    [JsonPropertyName("expires_in")]
    public long ExpiresIn { get; init; } = ExpiresIn is >= 1 and <= 900 ? ExpiresIn : throw new JsonException("Invalid token lifetime.");

    /// <summary>Redacts the token from diagnostics.</summary>
    public override string ToString() => "TerraScaleAuthEndUserToken { AccessToken = [REDACTED] }";
}

public sealed partial class TerraScaleAuthProjectsClient
{
    /// <summary>
    /// mintEndUserToken: POST /v1/terrascale/projects/{project}/end-user-tokens, sent once with the explicit whole
    /// project API key as Bearer. The key must match the project, have both documents:read and documents:write,
    /// and have no database restriction. Organization keys, platform JWTs and cookies cannot authenticate.
    /// The backend must verify the user and authorize the asserted roles; never relay browser-supplied claims.
    /// Only canonical user/project ULIDs and up to 64 case-sensitive roles (128 UTF-16 code units each, nonblank,
    /// no controls) are accepted. Null roles omits claims; duplicates are removed ordinally by the server.
    /// UTF-8 JSON is bounded to 16 KiB; the signed JWT is capped at 8192 ASCII bytes and lifetime at 900 seconds
    /// or key expiry. No user account is provisioned. Revocation stops future minting, not offline issued tokens.
    /// Answers JSON 200; 400 invalid_request, 401 unauthorized, 429 rate_limited (Retry-After), and 503 throw
    /// TerraScaleAuthException. No automatic retry, alternate grant, arbitrary claims or reserved-claim overrides.
    /// </summary>
    public Task<TerraScaleAuthEndUserToken> MintEndUserTokenAsync(string projectApiKey, string projectId, string userId,
        IReadOnlyList<string>? roles = null, CancellationToken cancellationToken = default)
    {
        TerraScaleIds.Validate(projectId);
        TerraScaleIds.Validate(userId);
        if (projectApiKey is not { Length: 79 } || projectApiKey[26] != '_' || !TerraScaleIds.IsValid(projectApiKey[..26])
            || projectApiKey[27..].Any(c => !"0123456789abcdefghjkmnpqrstvwxyz".Contains(c)))
            throw new ArgumentException("Supply the whole project API key: canonical ULID, underscore and 52 lowercase Base32 characters.", nameof(projectApiKey));
        var count = roles?.Count ?? 0;
        if (count > 64) throw new ArgumentException("At most 64 trusted roles are accepted.", nameof(roles));
        var snapshot = new string[count];
        for (var i = 0; i < count; i++)
        {
            var role = roles![i];
            if (string.IsNullOrWhiteSpace(role) || role.Length > 128 || role.Any(char.IsControl))
                throw new ArgumentException("Each trusted role must be nonblank, at most 128 UTF-16 code units, without controls.", nameof(roles));
            snapshot[i] = role;
        }
        var json = TerraScaleAuthEndUserTokenJsonContext.Default;
        return client.SendAuthenticatedJsonAsync(HttpMethod.Post, "/v1/terrascale/projects/" + projectId + "/end-user-tokens",
            new EndUserTokenMintRequest(userId, roles is null ? null : new(snapshot)), json.EndUserTokenMintRequest,
            json.TerraScaleAuthEndUserToken, TerraScaleAuthCredentials.Bearer(projectApiKey), HttpStatusCode.OK, 16 * 1024, cancellationToken);
    }
}

internal sealed record EndUserTokenMintRequest([property: JsonPropertyName("user_id")] string UserId,
    [property: JsonPropertyName("claims")] EndUserTokenMintClaims? Claims);
internal sealed record EndUserTokenMintClaims([property: JsonPropertyName("roles")] IReadOnlyList<string> Roles);

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, MaxDepth = 4,
    RespectRequiredConstructorParameters = true, RespectNullableAnnotations = true)]
[JsonSerializable(typeof(EndUserTokenMintRequest))]
[JsonSerializable(typeof(TerraScaleAuthEndUserToken))]
internal sealed partial class TerraScaleAuthEndUserTokenJsonContext : JsonSerializerContext;

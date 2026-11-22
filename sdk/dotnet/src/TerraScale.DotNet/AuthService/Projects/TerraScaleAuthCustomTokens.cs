using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

using TerraScale.DotNet.Errors;

namespace TerraScale.DotNet;

/// <summary>A backend-asserted custom token signed by the project tenant; no refresh token or configurable lifetime.</summary>
/// <param name="AccessToken">Opaque compact access token, at most 8192 ASCII bytes. Keep out of logs.</param>
/// <param name="TokenType">Bearer.</param>
/// <param name="ExpiresIn">Remaining seconds, at most 300.</param>
public sealed record TerraScaleAuthCustomToken(string AccessToken, string TokenType, long ExpiresIn)
{
    /// <summary>The sensitive access token.</summary>
    [JsonPropertyName("access_token")]
    public string AccessToken { get; init; } = AccessToken is { Length: > 0 and <= 8192 }
        && AccessToken.All(c => c is > ' ' and < '\x7f') ? AccessToken : throw new JsonException("Invalid custom access token.");

    /// <summary>The Bearer authorization scheme.</summary>
    [JsonPropertyName("token_type")]
    public string TokenType { get; init; } = TokenType == "Bearer" ? TokenType : throw new JsonException("Invalid token type.");

    /// <summary>Remaining lifetime in seconds; issued tokens validate offline until expiry plus server clock skew.</summary>
    [JsonPropertyName("expires_in")]
    public long ExpiresIn { get; init; } = ExpiresIn is >= 1 and <= 300 ? ExpiresIn : throw new JsonException("Invalid token lifetime.");

    /// <summary>Redacts the token from diagnostics.</summary>
    public override string ToString() => "TerraScaleAuthCustomToken { AccessToken = [REDACTED] }";
}

public sealed partial class TerraScaleAuthProjectsClient
{
    /// <summary>
    /// mintCustomToken: POST /v1/{project}/custom-tokens, sent once with the explicit whole project API key as Bearer.
    /// The key must belong to the project, have auth:tokens:mint and have no database restriction. Organization keys,
    /// platform JWTs and cookies cannot authenticate. The backend must verify the user and authorize the asserted
    /// claims; never relay browser-supplied claims. Trusted claims follow the server's 32-member, 1000-byte limit. The token lives 300 seconds. No user account is provisioned. Revocation stops future
    /// minting, not offline issued tokens. Answers JSON 200; 400 invalid_request, 401 unauthorized,
    /// 403 insufficient_scope, 404 not_found, 429 rate_limited (Retry-After), and 503 are refused with AuthRefused.
    /// </summary>
    public Task<AuthResult<TerraScaleAuthCustomToken>> MintCustomTokenAsync(string projectApiKey, string projectId, string userId,
        string? database = null, IReadOnlyDictionary<string, JsonElement>? claims = null, CancellationToken cancellationToken = default)
    {
        TerraScaleIds.Validate(projectId);
        TerraScaleIds.Validate(userId);
        var body = projectApiKey.StartsWith("sk_", StringComparison.Ordinal) ? projectApiKey[3..] : projectApiKey;
        if (body is not { Length: 79 } || body[26] != '_' || !TerraScaleIds.IsValid(body[..26])
            || body[27..].Any(c => !"0123456789abcdefghjkmnpqrstvwxyz".Contains(c)))
            throw new ArgumentException("Supply the whole project API key: optional sk_, a canonical ULID, an underscore and 52 lowercase Base32 characters.", nameof(projectApiKey));
        if (database is not null) TerraScaleIds.Validate(database);
        var json = TerraScaleAuthCustomTokenJsonContext.Default;
        return client.SendAuthenticatedJsonAsync(HttpMethod.Post, "/v1/" + projectId + "/custom-tokens",
            new CustomTokenMintRequest(userId, database, claims), json.CustomTokenMintRequest,
            json.TerraScaleAuthCustomToken, TerraScaleAuthCredentials.Bearer(projectApiKey), HttpStatusCode.OK, 16 * 1024, cancellationToken);
    }
}

internal sealed record CustomTokenMintRequest([property: JsonPropertyName("user_id")] string UserId,
    [property: JsonPropertyName("database")] string? Database,
    [property: JsonPropertyName("claims")] IReadOnlyDictionary<string, JsonElement>? Claims);

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, MaxDepth = 4,
    RespectRequiredConstructorParameters = true, RespectNullableAnnotations = true)]
[JsonSerializable(typeof(CustomTokenMintRequest))]
[JsonSerializable(typeof(TerraScaleAuthCustomToken))]
internal sealed partial class TerraScaleAuthCustomTokenJsonContext : JsonSerializerContext;

using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

using TerraScale.DotNet.Errors;

namespace TerraScale.DotNet;

public sealed record TerraScaleAuthCustomToken(string AccessToken, string TokenType, long ExpiresIn)
{
    [JsonPropertyName("access_token")]
    public string AccessToken { get; init; } = AccessToken is { Length: > 0 and <= 8192 }
        && AccessToken.All(c => c is > ' ' and < '\x7f') ? AccessToken : throw new JsonException("Invalid custom access token.");

    [JsonPropertyName("token_type")]
    public string TokenType { get; init; } = TokenType == "Bearer" ? TokenType : throw new JsonException("Invalid token type.");

    [JsonPropertyName("expires_in")]
    public long ExpiresIn { get; init; } = ExpiresIn is >= 1 and <= 300 ? ExpiresIn : throw new JsonException("Invalid token lifetime.");

    public override string ToString() => "TerraScaleAuthCustomToken { AccessToken = [REDACTED] }";
}

public sealed partial class TerraScaleAuthProjectsClient
{
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

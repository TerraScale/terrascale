using System.Text.Json.Serialization;
using TerraScale.Dashboard.Platform.TsAuth;

namespace TerraScale.Dashboard.Features.ApiKeys
{
    public record ProjectApiKeyMetadata(string KeyId, string ProjectId, string Name, string Prefix, string LastFour, IReadOnlyList<string> Scopes,
        string DatabaseId, string CreatedByEmail, string CreatedAt, string ExpiresAt, string LastUsedAt, string RevokedAt, bool IsActive)
    {
        public string CreatedByUserId { get; init; } = "";
        public string? DatabaseName { get; init; }
        public IReadOnlyList<string>? AllowedCidrs { get; init; }
    }
    public sealed record ProjectCredentialMetadata(string KeyId, string ProjectId, string Name, string Prefix, string LastFour, IReadOnlyList<string> Scopes,
        string DatabaseId, string CreatedByEmail, string CreatedAt, string ExpiresAt, string LastUsedAt, string RevokedAt, bool IsActive, string Kind)
        : ProjectApiKeyMetadata(KeyId, ProjectId, Name, Prefix, LastFour, Scopes, DatabaseId, CreatedByEmail, CreatedAt, ExpiresAt, LastUsedAt, RevokedAt, IsActive);
    public sealed record ProjectApiKeySecret(ProjectApiKeyMetadata ApiKey, string Secret)
    {
        public override string ToString()
        {
            return $"Project credential {ApiKey.KeyId} [REDACTED]";
        }
    }

    public sealed record CreateProjectCredentialRequest([property: JsonPropertyName("_csrf")] string Csrf,
        string Name, string? DatabaseId, string[]? Scope, string? Scopes, int? ExpiresInDays = null, string? AllowedCidrs = null);
    public sealed record RevokeProjectCredentialRequest([property: JsonPropertyName("_csrf")] string Csrf, string? KeyId);
    public sealed record RotateProjectCredentialRequest([property: JsonPropertyName("_csrf")] string Csrf, string KeyId, int? PreviousSecretTtl = null);

    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
    [JsonSerializable(typeof(ProjectCredentialMetadata[]))]
    [JsonSerializable(typeof(ProjectCredentialMetadata))]
    [JsonSerializable(typeof(ProjectApiKeySecret))]
    [JsonSerializable(typeof(CreateProjectCredentialRequest))]
    [JsonSerializable(typeof(RevokeProjectCredentialRequest))]
    [JsonSerializable(typeof(RotateProjectCredentialRequest))]
    internal sealed partial class ApiKeyContractJson : JsonSerializerContext;

    // Remaining allowlist update bodies are not yet exposed by the SDK.
    internal sealed record AuthAllowedCidrsBody([property: JsonPropertyName("allowed_cidrs")] IReadOnlyList<string> AllowedCidrs);

    [JsonSourceGenerationOptions(RespectNullableAnnotations = true)]
    [JsonSerializable(typeof(AuthAllowedCidrsBody))]
    [JsonSerializable(typeof(TsAuthPlatformApiKey))]
    internal sealed partial class ApiKeyAuthJson : JsonSerializerContext;
}

using System.Text.Json.Serialization;

namespace TerraScale.Dashboard.Platform.TsAuth
{
    public sealed record TsAuthPlatformPage<T>(
        [property: JsonPropertyName("items")] IReadOnlyList<T> Items,
        [property: JsonPropertyName("next")] string? Next);

    public sealed record TsAuthPlatformName([property: JsonPropertyName("name")] string? Name);

    public sealed record TsAuthPlatformRole([property: JsonPropertyName("role")] string? Role);

    public sealed record TsAuthPlatformOrganization(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("created_by")] string CreatedBy,
        [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
        [property: JsonPropertyName("role")] string? Role,
        [property: JsonPropertyName("require_mfa")] bool RequireMfa);

    public sealed record TsAuthPlatformMember(
        [property: JsonPropertyName("user_id")] string UserId,
        [property: JsonPropertyName("email")] string Email,
        [property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("joined_at")] DateTimeOffset JoinedAt);

    public sealed record TsAuthPlatformProject(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("organization_id")] string OrganizationId,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("issuer")] string? Issuer,
        [property: JsonPropertyName("created_by")] string CreatedBy,
        [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
        [property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("allowed_cidrs")] IReadOnlyList<string> AllowedCidrs);

    public sealed record TsAuthPlatformProjectMember(
        [property: JsonPropertyName("user_id")] string UserId,
        [property: JsonPropertyName("email")] string Email,
        [property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("granted_at")] DateTimeOffset GrantedAt);

    public sealed record TsAuthPlatformCreateApiKey(
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("scopes")] IReadOnlyList<string>? Scopes,
        [property: JsonPropertyName("database")] string? Database = null,
        [property: JsonPropertyName("expires_at")] DateTimeOffset? ExpiresAt = null,
        [property: JsonPropertyName("allowed_cidrs")] IReadOnlyList<string>? AllowedCidrs = null);

    public sealed record TsAuthPlatformRotateApiKey([property: JsonPropertyName("previous_secret_ttl")] int? PreviousSecretTtl = null);

    public sealed record TsAuthPlatformApiKey(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("organization_id")] string OrganizationId,
        [property: JsonPropertyName("project_id")] string? ProjectId,
        [property: JsonPropertyName("database")] string? Database,
        [property: JsonPropertyName("scopes")] IReadOnlyList<string> Scopes,
        [property: JsonPropertyName("created_by")] string CreatedBy,
        [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
        [property: JsonPropertyName("expires_at")] DateTimeOffset? ExpiresAt,
        [property: JsonPropertyName("previous_secret_expires_at")] DateTimeOffset? PreviousSecretExpiresAt,
        [property: JsonPropertyName("allowed_cidrs")] IReadOnlyList<string> AllowedCidrs,
        [property: JsonPropertyName("last_used_at")] DateTimeOffset? LastUsedAt = null);

    public sealed record TsAuthPlatformApiKeySecret(
        [property: JsonPropertyName("api_key")] TsAuthPlatformApiKey ApiKey,
        [property: JsonPropertyName("key")] string Key)
    {
        public override string ToString()
        {
            return "TsAuthPlatformApiKeySecret { sensitive fields redacted }";
        }
    }
}

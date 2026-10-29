using System.Text.Json.Serialization;

namespace TerraScale.DotNet;

/// <summary>A platform page. Pass Next unchanged as after on the same list; null ends pagination.</summary>
public sealed record TerraScaleAuthPlatformPage<T>(
    [property: JsonPropertyName("items")] IReadOnlyList<T> Items,
    [property: JsonPropertyName("next")] string? Next);

/// <summary>Wire DTO for the producer NameRequest. See contracts/auth/dtos.json.</summary>
public sealed record TerraScaleAuthPlatformName([property: JsonPropertyName("name")] string? Name);

/// <summary>Wire DTO for the producer RoleRequest. See contracts/auth/dtos.json.</summary>
public sealed record TerraScaleAuthPlatformRole([property: JsonPropertyName("role")] string? Role);

/// <summary>Wire DTO for the producer OrganizationBody, including the required RequireMfa security policy. See contracts/auth/dtos.json.</summary>
public sealed record TerraScaleAuthPlatformOrganization(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("created_by")] string CreatedBy,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("role")] string? Role,
    [property: JsonPropertyName("require_mfa")] bool RequireMfa);

/// <summary>Wire DTO for the producer MemberBody. See contracts/auth/dtos.json.</summary>
public sealed record TerraScaleAuthPlatformMember(
    [property: JsonPropertyName("user_id")] string UserId,
    [property: JsonPropertyName("email")] string Email,
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("joined_at")] DateTimeOffset JoinedAt);

/// <summary>Wire DTO for the producer ProjectBody. See contracts/auth/dtos.json.</summary>
public sealed record TerraScaleAuthPlatformProject(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("organization_id")] string OrganizationId,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("issuer")] string? Issuer,
    [property: JsonPropertyName("created_by")] string CreatedBy,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("allowed_cidrs")] IReadOnlyList<string> AllowedCidrs);

/// <summary>Wire DTO for the producer ProjectMemberBody. See contracts/auth/dtos.json.</summary>
public sealed record TerraScaleAuthPlatformProjectMember(
    [property: JsonPropertyName("user_id")] string UserId,
    [property: JsonPropertyName("email")] string Email,
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("granted_at")] DateTimeOffset GrantedAt);

/// <summary>Wire DTO for the producer CreateApiKeyRequest. See contracts/auth/dtos.json.</summary>
public sealed record TerraScaleAuthPlatformCreateApiKey(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("scopes")] IReadOnlyList<string>? Scopes,
    [property: JsonPropertyName("database")] string? Database = null,
    [property: JsonPropertyName("expires_at")] DateTimeOffset? ExpiresAt = null,
    [property: JsonPropertyName("allowed_cidrs")] IReadOnlyList<string>? AllowedCidrs = null);

/// <summary>Wire DTO for the producer RotateApiKeyRequest. See contracts/auth/dtos.json.</summary>
public sealed record TerraScaleAuthPlatformRotateApiKey([property: JsonPropertyName("previous_secret_ttl")] int? PreviousSecretTtl = null);

/// <summary>Wire DTO for the producer ApiKeyBody. See contracts/auth/dtos.json.</summary>
public sealed record TerraScaleAuthPlatformApiKey(
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

/// <summary>Wire DTO for the producer ApiKeySecretBody. See contracts/auth/dtos.json.</summary>
public sealed record TerraScaleAuthPlatformApiKeySecret(
    [property: JsonPropertyName("api_key")] TerraScaleAuthPlatformApiKey ApiKey,
    [property: JsonPropertyName("key")] string Key)
{
    /// <inheritdoc />
    public override string ToString() => "TerraScaleAuthPlatformApiKeySecret { sensitive fields redacted }";
}

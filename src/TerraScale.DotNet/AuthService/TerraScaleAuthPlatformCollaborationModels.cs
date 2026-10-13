using System.Text.Json.Serialization;

namespace TerraScale.DotNet;

/// <summary>Wire DTO for the producer CreateInvitationRequest. See contracts/auth/dtos.json.</summary>
public sealed record TerraScaleAuthPlatformCreateInvitation(
    [property: JsonPropertyName("email")] string? Email,
    [property: JsonPropertyName("role")] string? Role,
    [property: JsonPropertyName("project_id")] string? ProjectId = null);

/// <summary>Wire DTO for the producer InvitationTokenRequest. See contracts/auth/dtos.json.</summary>
public sealed record TerraScaleAuthPlatformInvitationToken([property: JsonPropertyName("token")] string? Token)
{
    /// <inheritdoc />
    public override string ToString() => "TerraScaleAuthPlatformInvitationToken { sensitive fields redacted }";
}

/// <summary>Wire DTO for the producer StartTransferRequest. See contracts/auth/dtos.json.</summary>
public sealed record TerraScaleAuthPlatformStartOwnershipTransfer([property: JsonPropertyName("user_id")] string? UserId);

/// <summary>Wire DTO for the producer InvitationBody. See contracts/auth/dtos.json.</summary>
public sealed record TerraScaleAuthPlatformInvitation(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("email")] string Email,
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("project_id")] string? ProjectId,
    [property: JsonPropertyName("invited_by")] string InvitedBy,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("expires_at")] DateTimeOffset ExpiresAt,
    [property: JsonPropertyName("status")] string Status);

/// <summary>Wire DTO for the producer InvitationCreatedBody. See contracts/auth/dtos.json.</summary>
public sealed record TerraScaleAuthPlatformCreatedInvitation(
    [property: JsonPropertyName("invitation")] TerraScaleAuthPlatformInvitation Invitation,
    [property: JsonPropertyName("token")] string Token)
{
    /// <inheritdoc />
    public override string ToString() => "TerraScaleAuthPlatformCreatedInvitation { sensitive fields redacted }";
}

/// <summary>Wire DTO for the producer NamedBody. See contracts/auth/dtos.json.</summary>
public sealed record TerraScaleAuthPlatformNamed(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name);

/// <summary>Wire DTO for the producer InvitationLookupBody. See contracts/auth/dtos.json.</summary>
public sealed record TerraScaleAuthPlatformInvitationLookup(
    [property: JsonPropertyName("organization")] TerraScaleAuthPlatformNamed Organization,
    [property: JsonPropertyName("project")] TerraScaleAuthPlatformNamed? Project,
    [property: JsonPropertyName("email")] string Email,
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("expires_at")] DateTimeOffset ExpiresAt);

/// <summary>Wire DTO for the producer AcceptedInvitationBody. See contracts/auth/dtos.json.</summary>
public sealed record TerraScaleAuthPlatformAcceptedInvitation(
    [property: JsonPropertyName("organization_id")] string OrganizationId,
    [property: JsonPropertyName("project_id")] string? ProjectId,
    [property: JsonPropertyName("role")] string Role);

/// <summary>Wire DTO for the producer OwnershipTransferBody. See contracts/auth/dtos.json.</summary>
public sealed record TerraScaleAuthPlatformOwnershipTransfer(
    [property: JsonPropertyName("from_user_id")] string FromUserId,
    [property: JsonPropertyName("to_user_id")] string ToUserId,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("expires_at")] DateTimeOffset ExpiresAt);

/// <summary>Wire DTO for the producer AuditEventBody. See contracts/auth/dtos.json.</summary>
public sealed record TerraScaleAuthPlatformAuditEvent(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("actor")] string Actor,
    [property: JsonPropertyName("action")] string Action,
    [property: JsonPropertyName("target")] string? Target,
    [property: JsonPropertyName("detail")] string? Detail,
    [property: JsonPropertyName("occurred_at")] DateTimeOffset OccurredAt);

using System.Text.Json.Serialization;

namespace TerraScale.Authentication.Contracts;

public sealed record OrganizationRecord(string Id, string InstanceId, string Name, string CreatedBy, DateTimeOffset CreatedAt, int Owners, int Projects, OwnershipTransferRecord? Transfer = null, bool RequireMfa = false)
{
    public string? Version { get; init; }
}

public sealed record OwnershipTransferRecord(string FromUserId, string ToUserId, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt);

public sealed record MembershipRecord(string InstanceId, string OrganizationId, string UserId, string? Role, DateTimeOffset JoinedAt)
{
    public string? Version { get; init; }
}

public sealed record ProjectRecord(string Id, string InstanceId, string OrganizationId, string Name, string CreatedBy, DateTimeOffset CreatedAt, string? ClosureId = null, IReadOnlyList<string>? AllowedCidrs = null)
{
    public string? Version { get; init; }
}

public sealed record ProjectMemberRecord(string InstanceId, string OrganizationId, string ProjectId, string UserId, string Role, DateTimeOffset GrantedAt)
{
    public string? Version { get; init; }
}

public sealed record ApiKeyRecord(
    string Id,
    string InstanceId,
    string OrganizationId,
    string? ProjectId,
    string? Database,
    string Name,
    IReadOnlyList<string> Scopes,
    string SecretHash,
    string? PreviousSecretHash,
    DateTimeOffset? PreviousSecretExpiresAt,
    string CreatedBy,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ExpiresAt,
    IReadOnlyList<string>? AllowedCidrs = null)
{
    public string? Version { get; init; }

    public IReadOnlyList<CredentialBinding> SecretBindings { get; init; } = [];

    public IReadOnlyList<CredentialBinding> PreviousSecretBindings { get; init; } = [];

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ParentKeyId { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ParentGeneration { get; init; }
}

public sealed record ApiKeyUsageRecord(string KeyId, string InstanceId, string OrganizationId, string? ProjectId, DateTimeOffset LastUsedAt)
{
    public string? Version { get; init; }
}

public sealed record InvitationRecord(
    string Id,
    string InstanceId,
    string OrganizationId,
    string? ProjectId,
    string Email,
    string Role,
    string InvitedBy,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    string Status,
    string TokenHash,
    DateTimeOffset? DecidedAt = null,
    string? DecidedBy = null)
{
    public string? Version { get; init; }
}

public sealed record AuditEventRecord(string Id, string InstanceId, string OrganizationId, string Actor, string Action, string? Target, string? Detail, DateTimeOffset OccurredAt);

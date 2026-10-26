namespace TerraScale.Authentication.Contracts;

/// <summary>An organization of the platform tenant (collection <c>organizations</c>).</summary>
/// <param name="Id">Canonical ULID.</param>
/// <param name="InstanceId">Always the platform tenant.</param>
/// <param name="Name">Display name; not unique.</param>
/// <param name="CreatedBy">The creating user.</param>
/// <param name="CreatedAt">Creation time.</param>
/// <param name="Owners">Members with the owner role; every owner change writes it, so the last owner is never lost.</param>
/// <param name="Projects">Its projects; every project creation and deletion writes it.</param>
/// <param name="Transfer">The pending ownership transfer, if any.</param>
/// <param name="RequireMfa">Whether members must have an active second factor to reach the organization.</param>
public sealed record OrganizationRecord(string Id, string InstanceId, string Name, string CreatedBy, DateTimeOffset CreatedAt, int Owners, int Projects, OwnershipTransferRecord? Transfer = null, bool RequireMfa = false);

/// <summary>An offer of an organization's ownership to one of its members, confirmed by that member.</summary>
/// <param name="FromUserId">The owner who offered it; it lapses once that user stops being an owner.</param>
/// <param name="ToUserId">The nominee.</param>
/// <param name="CreatedAt">When it was offered.</param>
/// <param name="ExpiresAt">Seven days later.</param>
public sealed record OwnershipTransferRecord(string FromUserId, string ToUserId, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt);

/// <summary>
/// A user's place in an organization (collection <c>memberships</c>, addressed by organization and user): a member's
/// organization role, or a collaborator's record without one. Every write that gives or takes a project role also
/// rewrites or deletes this record, so a removal and a grant never both succeed on stale reads.
/// </summary>
/// <param name="InstanceId">Always the platform tenant.</param>
/// <param name="OrganizationId">The organization.</param>
/// <param name="UserId">The member or collaborator.</param>
/// <param name="Role"><c>viewer</c>, <c>member</c>, <c>admin</c> or <c>owner</c>; null for a collaborator.</param>
/// <param name="JoinedAt">When the user joined.</param>
public sealed record MembershipRecord(string InstanceId, string OrganizationId, string UserId, string? Role, DateTimeOffset JoinedAt);

/// <summary>A project (collection <c>projects</c>); its id is also its end users' tenant.</summary>
/// <param name="Id">Canonical ULID.</param>
/// <param name="InstanceId">Always the platform tenant.</param>
/// <param name="OrganizationId">The owning organization; never changes.</param>
/// <param name="Name">Display name, unique in the organization ignoring case.</param>
/// <param name="CreatedBy">The creating user.</param>
/// <param name="CreatedAt">Creation time.</param>
/// <param name="ClosureId">
/// The opaque ULID TerraBase knows this project's closure by, drawn at random by its first closure intent and reused by
/// every later one (docs/auth/project-closure.md); null until then.
/// </param>
/// <param name="AllowedCidrs">Canonical CIDRs every API key token of the project must be used from; null or empty allows any address.</param>
public sealed record ProjectRecord(string Id, string InstanceId, string OrganizationId, string Name, string CreatedBy, DateTimeOffset CreatedAt, string? ClosureId = null, IReadOnlyList<string>? AllowedCidrs = null);

/// <summary>An explicit project role (collection <c>project_members</c>, addressed by project and user).</summary>
/// <param name="InstanceId">Always the platform tenant.</param>
/// <param name="OrganizationId">The project's organization.</param>
/// <param name="ProjectId">The project.</param>
/// <param name="UserId">The user; an organization member or a collaborator.</param>
/// <param name="Role"><c>viewer</c>, <c>member</c> or <c>admin</c>.</param>
/// <param name="GrantedAt">When the role was first granted.</param>
public sealed record ProjectMemberRecord(string InstanceId, string OrganizationId, string ProjectId, string UserId, string Role, DateTimeOffset GrantedAt);

/// <summary>
/// An API key (collection <c>api_keys</c>, addressed by its id): an OAuth client credential owned by an organization or a
/// project. Only SHA-256 digests of the key string are stored. Revoking deletes the record.
/// </summary>
/// <param name="Id">Canonical ULID: the client id and the <c>sub</c> of its tokens.</param>
/// <param name="InstanceId">Always the platform tenant.</param>
/// <param name="OrganizationId">The owning organization.</param>
/// <param name="ProjectId">The owning project, or null for an organization key.</param>
/// <param name="Database">A TerraBase database name the key is narrowed to, or null.</param>
/// <param name="Name">Display name.</param>
/// <param name="Scopes">The TerraBase scopes its tokens may carry.</param>
/// <param name="SecretHash">Lowercase hex SHA-256 of the whole key string.</param>
/// <param name="PreviousSecretHash">The digest before the last rotation, while it still works.</param>
/// <param name="PreviousSecretExpiresAt">Until when the previous key string works.</param>
/// <param name="CreatedBy">The creating user.</param>
/// <param name="CreatedAt">Creation time.</param>
/// <param name="ExpiresAt">Optional expiry; an expired key is refused like a revoked one.</param>
/// <param name="AllowedCidrs">Canonical CIDRs its tokens must be used from; null or empty allows any address.</param>
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
    /// <summary>Fresh primary credential winners for the current secret, including its issuing user state.</summary>
    public IReadOnlyList<CredentialBinding> SecretBindings { get; init; } = [];

    /// <summary>The previous secret's own winners during rotation grace; never replaced with the new secret's lineage.</summary>
    public IReadOnlyList<CredentialBinding> PreviousSecretBindings { get; init; } = [];
}

/// <summary>
/// When an API key last exchanged a token (collection <c>api_key_usage</c>, addressed by key id). It is kept apart from the
/// key so a usage write never races a rotation or revocation of the key record itself.
/// </summary>
/// <param name="KeyId">The key.</param>
/// <param name="InstanceId">Always the platform tenant.</param>
/// <param name="OrganizationId">The key's organization.</param>
/// <param name="ProjectId">The key's project; null for an organization key.</param>
/// <param name="LastUsedAt">The last successful token exchange, at most <c>ApiKeys.UsageGranularity</c> stale.</param>
public sealed record ApiKeyUsageRecord(string KeyId, string InstanceId, string OrganizationId, string? ProjectId, DateTimeOffset LastUsedAt);

/// <summary>
/// An invitation (collection <c>invitations</c>, addressed by id) to an organization role, or with a project to that
/// project's role. Only the SHA-256 of its token is stored; <c>uniques</c> maps that digest to the invitation and holds
/// one pending invitation per email and target.
/// </summary>
/// <param name="Id">Canonical ULID.</param>
/// <param name="InstanceId">Always the platform tenant.</param>
/// <param name="OrganizationId">The organization.</param>
/// <param name="ProjectId">The project, for a project invitation.</param>
/// <param name="Email">The invitee's normalized address.</param>
/// <param name="Role">An organization role, or with a project a project role.</param>
/// <param name="InvitedBy">The inviting user.</param>
/// <param name="CreatedAt">Creation time.</param>
/// <param name="ExpiresAt">Seven days later.</param>
/// <param name="Status"><c>pending</c>, <c>accepted</c>, <c>declined</c> or <c>revoked</c>.</param>
/// <param name="TokenHash">Lowercase hex SHA-256 of the token.</param>
/// <param name="DecidedAt">When it stopped being pending.</param>
/// <param name="DecidedBy">Who accepted, declined or revoked it.</param>
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
    string? DecidedBy = null);

/// <summary>An audit event of an organization (collection <c>audit_events</c>), written with the change it records.</summary>
/// <param name="Id">Monotonic canonical ULID.</param>
/// <param name="InstanceId">Always the platform tenant.</param>
/// <param name="OrganizationId">The organization.</param>
/// <param name="Actor">The acting user, or a platform client's id.</param>
/// <param name="Action">For example <c>member.role_changed</c>.</param>
/// <param name="Target">A user id, email, project id or key id.</param>
/// <param name="Detail">The new role, name, scopes or reason.</param>
/// <param name="OccurredAt">When it happened.</param>
public sealed record AuditEventRecord(string Id, string InstanceId, string OrganizationId, string Actor, string Action, string? Target, string? Detail, DateTimeOffset OccurredAt);

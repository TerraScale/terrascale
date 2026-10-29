using System.Text.Json.Serialization;

namespace TerraScale.DotNet.TerraBase;

/// <summary>An organization's plan. Stored only; TerraBase enforces nothing from it yet.</summary>
[JsonConverter(typeof(TerraBaseStringEnumConverter<TerraBasePlan>))]
public enum TerraBasePlan
{
    /// <summary>The default plan.</summary>
    [JsonStringEnumMemberName("hobby")]
    Hobby,

    /// <summary>Paid plan.</summary>
    [JsonStringEnumMemberName("pro")]
    Pro,

    /// <summary>Contracted plan.</summary>
    [JsonStringEnumMemberName("enterprise")]
    Enterprise,
}

/// <summary>A member's role in an organization, from least to most privileged.</summary>
[JsonConverter(typeof(TerraBaseStringEnumConverter<TerraBaseRole>))]
public enum TerraBaseRole
{
    /// <summary>Manages projects and databases; reads members and the pending ownership transfer.</summary>
    [JsonStringEnumMemberName("member")]
    Member,

    /// <summary>Also renames the organization, manages members and invitations, and reads and configures the audit log.</summary>
    [JsonStringEnumMemberName("admin")]
    Admin,

    /// <summary>Also deletes the organization and grants, revokes and transfers ownership.</summary>
    [JsonStringEnumMemberName("owner")]
    Owner,
}

internal sealed class TerraBaseStringEnumConverter<T> : JsonStringEnumConverter<T> where T : struct, Enum
{
    public TerraBaseStringEnumConverter() : base(allowIntegerValues: false) { }
}

/// <summary>One page of a Management API list.</summary>
/// <typeparam name="T">Item type.</typeparam>
/// <param name="Items">Items of this page.</param>
/// <param name="Next">Pass as <c>after</c> to read the next page; <see langword="null"/> on the last page.</param>
public sealed record TerraBasePage<T>(IReadOnlyList<T> Items, string? Next) : IJsonOnDeserialized
{
    void IJsonOnDeserialized.OnDeserialized() => TerraBaseJsonContext.RequireNonNullItems(Items);
}

/// <summary>An organization: the tenant, which owns billing and projects.</summary>
/// <param name="Id">Canonical uppercase ULID.</param>
/// <param name="Name">Display name.</param>
/// <param name="CreatedBy">ts-auth user id of the creator.</param>
/// <param name="CreatedAt">Creation time.</param>
/// <param name="Plan">The organization's plan.</param>
public sealed record TerraBaseOrganization(string Id, string Name, string CreatedBy, DateTimeOffset CreatedAt, TerraBasePlan Plan);

/// <summary>A project: a folder of databases inside an organization.</summary>
/// <param name="Id">Canonical uppercase ULID; the Documents API's <c>{project}</c>.</param>
/// <param name="OrganizationId">Owning organization.</param>
/// <param name="Name">Display name, unique within the organization.</param>
/// <param name="CreatedAt">Creation time.</param>
public sealed record TerraBaseProject(string Id, string OrganizationId, string Name, DateTimeOffset CreatedAt);

/// <summary>Where a project closure stands in TerraBase.</summary>
/// <param name="Status"><c>accepted</c> once every catalog holder confirmed the project empty under the fence; otherwise <c>pending</c>.</param>
/// <param name="ClosureId">The closure id the fence was written with.</param>
/// <param name="PendingHolders">TerraBase machines that have not confirmed the project empty under the fence.</param>
/// <param name="EvidenceVersion">Closure evidence version; absent legacy responses default to 0. Destructive consumers must require version 2 explicitly.</param>
public sealed record TerraBaseProjectClosure(string Status, string ClosureId, int PendingHolders, int EvidenceVersion = 0)
{
    /// <summary>Whether the returned status is accepted. Does not validate <see cref="EvidenceVersion"/>.</summary>
    public bool Accepted => this.Status == "accepted";
}

/// <summary>Where a shared-history erasure stands in TerraBase, computed from every holder; never complete while one is missing.</summary>
/// <param name="Status"><c>fencing</c>, <c>settling</c>, <c>reverify</c>, <c>rewriting</c>, <c>residue</c>, <c>backup</c> or <c>complete</c>.</param>
/// <param name="ErasureId">The random erasure id of ts-auth's deletion job.</param>
/// <param name="PendingHolders">Holders that have not reached the current phase.</param>
/// <param name="ActiveDueAt">The request time plus 30 days.</param>
/// <param name="BackupDueAt">The request time plus the configured backup retention plus 30 days.</param>
/// <param name="ActiveOverdue">Whether active copies are still pending past <paramref name="ActiveDueAt"/>.</param>
/// <param name="BackupOverdue">Whether completion is still pending past <paramref name="BackupDueAt"/>.</param>
/// <param name="SettlementToken">With <c>reverify</c>: present it after re-verifying auth's cleanup against this TerraBase.</param>
public sealed record TerraBaseHistoryErasure(
    string Status, string ErasureId, int PendingHolders, DateTimeOffset ActiveDueAt, DateTimeOffset BackupDueAt, bool ActiveOverdue, bool BackupOverdue,
    string? SettlementToken = null)
{
    /// <summary>Whether every holder rewrote both files, cleared identifier residue, and no older backup remains.</summary>
    public bool Complete => this.Status == "complete";
}

/// <summary>A database registered in a project. The Documents API addresses it by <see cref="ProjectId"/> and <see cref="Name"/>.</summary>
/// <param name="Id">Canonical uppercase ULID.</param>
/// <param name="ProjectId">Owning project.</param>
/// <param name="Name">Database name, unique within the project.</param>
/// <param name="BranchedFrom">Id of the database this one was branched from, if any.</param>
/// <param name="CreatedAt">Creation time.</param>
public sealed record TerraBaseManagedDatabase(string Id, string ProjectId, string Name, string? BranchedFrom, DateTimeOffset CreatedAt) : IJsonOnDeserialized
{
    /// <summary>The region that answered.</summary>
    [JsonRequired]
    public string Region { get; init; } = null!;

    /// <summary>The region where the database is created and managed.</summary>
    [JsonRequired]
    public string HomeRegion { get; init; } = null!;

    /// <summary>The other regions holding a full copy and serving the database.</summary>
    [JsonRequired]
    public IReadOnlyList<string> ReplicaRegions { get; init; } = null!;

    /// <summary>Whether every serving region holds a full copy, including regions that join later; then <see cref="ReplicaRegions"/> is empty.</summary>
    [JsonRequired]
    public bool AllRegions { get; init; }

    /// <summary>The name lost during a cross-region name conflict, or null when no conflict is reported.</summary>
    public string? PreviousName { get; init; }

    /// <summary>The immutable database id that kept <see cref="PreviousName"/>, or null.</summary>
    public string? ConflictOf { get; init; }

    void IJsonOnDeserialized.OnDeserialized() => TerraBaseJsonContext.RequireNonNullItems(ReplicaRegions);
}

/// <summary>A user's membership in an organization.</summary>
/// <param name="UserId">ts-auth user id.</param>
/// <param name="Role">Role in the organization.</param>
/// <param name="JoinedAt">When the user became a member.</param>
public sealed record TerraBaseMember(string UserId, TerraBaseRole Role, DateTimeOffset JoinedAt);

/// <summary>An invitation to join an organization, including terminal invitations.</summary>
/// <param name="Id">Opaque id.</param>
/// <param name="Email">The invited address.</param>
/// <param name="Role">Role on accepting.</param>
/// <param name="InvitedBy">User id of the inviter.</param>
/// <param name="CreatedAt">Creation time.</param>
/// <param name="ExpiresAt">After this time the invitation can no longer be accepted.</param>
/// <param name="Status">pending, accepted, declined or revoked; a pending invitation past ExpiresAt is expired.</param>
public sealed record TerraBaseInvitation(string Id, string Email, TerraBaseRole Role, string InvitedBy, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, string Status);

/// <summary>A new invitation and its acceptance token, which TerraBase returns only once.</summary>
/// <param name="Invitation">The invitation.</param>
/// <param name="Token">Secret the invitee presents to accept; deliver it in the invitation link.</param>
public sealed record TerraBaseCreatedInvitation(TerraBaseInvitation Invitation, string Token)
{
    /// <inheritdoc />
    public override string ToString() => $"TerraBaseCreatedInvitation {{ Invitation = {Invitation}, Token = [REDACTED] }}";
}

/// <summary>An owner's pending offer to hand ownership to another member.</summary>
/// <param name="FromUserId">The owner who started the transfer.</param>
/// <param name="ToUserId">The member who becomes owner on confirming.</param>
/// <param name="CreatedAt">Start time.</param>
/// <param name="ExpiresAt">After this time the transfer no longer exists.</param>
public sealed record TerraBaseOwnershipTransfer(string FromUserId, string ToUserId, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt);

/// <summary>How long an organization keeps its audit events.</summary>
/// <param name="Days">1 to 90 days.</param>
public sealed record TerraBaseAuditRetention(int Days);

/// <summary>One recorded access change.</summary>
/// <param name="Id">Opaque id.</param>
/// <param name="Actor">User id of the user who made the change.</param>
/// <param name="Action">What happened, for example <c>member.role_changed</c>.</param>
/// <param name="Target">What it happened to: a user id or an invitation email.</param>
/// <param name="Detail">The new value, such as a role, a number of days or a plan.</param>
/// <param name="OccurredAt">When it happened.</param>
public sealed record TerraBaseAuditEvent(string Id, string Actor, string Action, string? Target, string? Detail, DateTimeOffset OccurredAt);

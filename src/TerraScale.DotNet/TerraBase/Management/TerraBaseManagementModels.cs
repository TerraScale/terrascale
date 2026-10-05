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
/// <param name="Id">Lowercase hyphenated UUIDv7.</param>
/// <param name="Name">Display name.</param>
/// <param name="CreatedBy">ts-auth user id of the creator.</param>
/// <param name="CreatedAt">Creation time.</param>
/// <param name="Plan">The organization's plan.</param>
public sealed record TerraBaseOrganization(string Id, string Name, string CreatedBy, DateTimeOffset CreatedAt, TerraBasePlan Plan);

/// <summary>A project: a folder of databases inside an organization.</summary>
/// <param name="Id">Lowercase hyphenated UUIDv7; the Documents API's <c>{project}</c>.</param>
/// <param name="OrganizationId">Owning organization.</param>
/// <param name="Name">Display name, unique within the organization.</param>
/// <param name="CreatedAt">Creation time.</param>
public sealed record TerraBaseProject(string Id, string OrganizationId, string Name, DateTimeOffset CreatedAt);

/// <summary>A database registered in a project. The Documents API addresses it by <see cref="ProjectId"/> and <see cref="Name"/>.</summary>
/// <param name="Id">Lowercase hyphenated UUIDv7.</param>
/// <param name="ProjectId">Owning project.</param>
/// <param name="Name">Database name, unique within the project.</param>
/// <param name="BranchedFrom">Id of the database this one was branched from, if any.</param>
/// <param name="CreatedAt">Creation time.</param>
public sealed record TerraBaseManagedDatabase(string Id, string ProjectId, string Name, string? BranchedFrom, DateTimeOffset CreatedAt);

/// <summary>A user's membership in an organization.</summary>
/// <param name="UserId">ts-auth user id.</param>
/// <param name="Role">Role in the organization.</param>
/// <param name="JoinedAt">When the user became a member.</param>
public sealed record TerraBaseMember(string UserId, TerraBaseRole Role, DateTimeOffset JoinedAt);

/// <summary>A pending invitation to join an organization.</summary>
/// <param name="Id">Opaque id.</param>
/// <param name="Email">The invited address.</param>
/// <param name="Role">Role on accepting.</param>
/// <param name="InvitedBy">User id of the inviter.</param>
/// <param name="CreatedAt">Creation time.</param>
/// <param name="ExpiresAt">After this time the invitation can no longer be accepted.</param>
public sealed record TerraBaseInvitation(string Id, string Email, TerraBaseRole Role, string InvitedBy, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt);

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

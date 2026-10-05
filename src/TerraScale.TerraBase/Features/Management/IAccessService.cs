using System.Text.Json.Serialization;
using TerraScale.TerraBase.Errors;

namespace TerraScale.TerraBase.Features.Management;

/// <summary>A member's role in an organization, from least to most privileged. Numbers keep their meaning from before viewer.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<OrganizationRole>))]
public enum OrganizationRole
{
    /// <summary>Reads the organization, its projects, databases, members and pending ownership transfer; changes nothing but its own membership.</summary>
    [JsonStringEnumMemberName("viewer")]
    Viewer = -1,

    /// <summary>Also manages projects and databases.</summary>
    [JsonStringEnumMemberName("member")]
    Member,

    /// <summary>Also renames the organization, manages admins, members and invitations, and reads and configures the audit log.</summary>
    [JsonStringEnumMemberName("admin")]
    Admin,

    /// <summary>Also deletes the organization and grants, revokes and transfers ownership. An organization always keeps at least one.</summary>
    [JsonStringEnumMemberName("owner")]
    Owner,
}

/// <summary>A user's membership in an organization.</summary>
/// <param name="UserId">The ts-auth user id.</param>
/// <param name="Role">Role in the organization.</param>
/// <param name="JoinedAt">When the user became a member.</param>
public sealed record OrganizationMember(string UserId, OrganizationRole Role, DateTimeOffset JoinedAt);

/// <summary>Where an invitation is in its life. A pending invitation past its expiry is expired.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<InvitationStatus>))]
public enum InvitationStatus
{
    /// <summary>Can be accepted or declined until it expires.</summary>
    [JsonStringEnumMemberName("pending")]
    Pending,

    /// <summary>The invitee became a member.</summary>
    [JsonStringEnumMemberName("accepted")]
    Accepted,

    /// <summary>The invitee declined.</summary>
    [JsonStringEnumMemberName("declined")]
    Declined,

    /// <summary>An admin revoked it.</summary>
    [JsonStringEnumMemberName("revoked")]
    Revoked,
}

/// <summary>An invitation to join an organization.</summary>
/// <param name="Id">Lowercase hyphenated UUID.</param>
/// <param name="Email">The invited address; only a user with this email can accept.</param>
/// <param name="Role">Role the user gets on accepting.</param>
/// <param name="InvitedBy">User id of the inviter.</param>
/// <param name="CreatedAt">Creation time, or the last resend.</param>
/// <param name="ExpiresAt">After this time the invitation can no longer be accepted.</param>
/// <param name="Status">Its state; stored invitations from before statuses are pending.</param>
public sealed record Invitation(
    string Id, string Email, OrganizationRole Role, string InvitedBy, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt,
    InvitationStatus Status = InvitationStatus.Pending);

/// <summary>A pending invitation found by its token hash, with only the identity of its organization.</summary>
/// <param name="Organization">The organization's id and name.</param>
/// <param name="Invitation">The invitation.</param>
public sealed record FoundInvitation(OrganizationIdentity Organization, Invitation Invitation);

/// <summary>An organization's id and name, which an invitee may see before joining.</summary>
/// <param name="Id">Organization id.</param>
/// <param name="Name">Organization name.</param>
public sealed record OrganizationIdentity(string Id, string Name);

/// <summary>A user's new membership, with the organization it is in.</summary>
/// <param name="OrganizationId">The organization joined.</param>
/// <param name="Member">The membership.</param>
public sealed record Membership(string OrganizationId, OrganizationMember Member);

/// <summary>A new invitation and its acceptance token, which is returned only here.</summary>
/// <param name="Invitation">The invitation.</param>
/// <param name="Token">Secret the invitee presents to accept; deliver it in the invitation link.</param>
public sealed record CreatedInvitation(Invitation Invitation, string Token);

/// <summary>An owner's pending offer to hand ownership to another member.</summary>
/// <param name="FromUserId">The owner who started the transfer.</param>
/// <param name="ToUserId">The member who becomes owner on confirming.</param>
/// <param name="CreatedAt">Start time.</param>
/// <param name="ExpiresAt">After this time the transfer no longer exists.</param>
public sealed record OwnershipTransfer(string FromUserId, string ToUserId, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt);

/// <summary>How long an organization keeps its audit events.</summary>
/// <param name="Days">1 to 90 days.</param>
public sealed record AuditRetention(int Days);

/// <summary>One recorded access change.</summary>
/// <param name="Id">Opaque id; ids sort newest first.</param>
/// <param name="Actor">User id of the user who made the change.</param>
/// <param name="Action">What happened, for example <c>member.role_changed</c>.</param>
/// <param name="Target">What it happened to: a user id or an invitation email.</param>
/// <param name="Detail">The new value, such as a role or a number of days, when the action has one.</param>
/// <param name="OccurredAt">When it happened.</param>
public sealed record AuditEvent(string Id, string Actor, string Action, string? Target, string? Detail, DateTimeOffset OccurredAt);

/// <summary>The outcomes of <see cref="IAccessService.AddMemberAsync"/>.</summary>
public readonly union AddMemberResult(OrganizationMember, NotFound, Forbidden);

/// <summary>
/// Members, invitations, ownership transfers and the audit log of organizations.
/// </summary>
/// <remarks>
/// Every method acts as <c>userId</c> and is authorized by that user's role in the organization, in the same
/// transaction as the change. An organization the user is not a member of is <see cref="ManagementError.NotFound"/>,
/// as in <see cref="IManagementService"/>; a member whose role is too low gets <see cref="ManagementError.Forbidden"/>.
/// </remarks>
public interface IAccessService
{
    /// <summary>Lists members in user id order. Needs <see cref="OrganizationRole.Viewer"/>.</summary>
    ValueTask<ManagementResult<ManagementPage<OrganizationMember>>> ListMembersAsync(
        string userId, string organizationId, string? after, int limit, CancellationToken cancellationToken);

    /// <summary>
    /// Changes a member's role. Needs <see cref="OrganizationRole.Admin"/>, and <see cref="OrganizationRole.Owner"/>
    /// when the member is or becomes an owner. The last owner cannot be demoted.
    /// </summary>
    ValueTask<ManagementResult<OrganizationMember>> ChangeRoleAsync(
        string userId, string organizationId, string memberId, OrganizationRole role, CancellationToken cancellationToken);

    /// <summary>
    /// Adds <paramref name="memberId"/> as a <see cref="OrganizationRole.Member"/> without an invitation, recording
    /// <paramref name="email"/> in the audit log. Needs <see cref="OrganizationRole.Admin"/>. An existing member is returned
    /// unchanged. Served only on the administrative listener (TS-535).
    /// </summary>
    ValueTask<AddMemberResult> AddMemberAsync(
        string userId, string organizationId, string memberId, string email, CancellationToken cancellationToken);

    /// <summary>Removes a member, with the same role rules as <see cref="ChangeRoleAsync"/>. The last owner cannot be removed.</summary>
    ValueTask<ManagementError> RemoveMemberAsync(string userId, string organizationId, string memberId, CancellationToken cancellationToken);

    /// <summary>Removes the acting user from the organization, unless it is the last owner.</summary>
    ValueTask<ManagementError> LeaveAsync(string userId, string organizationId, CancellationToken cancellationToken);

    /// <summary>
    /// Invites an email address, replacing an expired invitation for it. Needs <see cref="OrganizationRole.Admin"/>,
    /// and <see cref="OrganizationRole.Owner"/> to invite an owner.
    /// </summary>
    ValueTask<ManagementResult<CreatedInvitation>> InviteAsync(
        string userId, string organizationId, string email, OrganizationRole role, CancellationToken cancellationToken);

    /// <summary>Lists every invitation with its status, in creation order. Needs <see cref="OrganizationRole.Admin"/>.</summary>
    ValueTask<ManagementResult<ManagementPage<Invitation>>> ListInvitationsAsync(
        string userId, string organizationId, string? after, int limit, CancellationToken cancellationToken);

    /// <summary>Marks a pending invitation revoked. Needs <see cref="OrganizationRole.Admin"/>.</summary>
    ValueTask<ManagementError> RevokeInvitationAsync(string userId, string organizationId, string invitationId, CancellationToken cancellationToken);

    /// <summary>Makes the user, whose verified email is <paramref name="email"/>, a member through an invitation token.</summary>
    ValueTask<ManagementResult<OrganizationMember>> AcceptInvitationAsync(string userId, string email, string token, CancellationToken cancellationToken);

    /// <summary>Finds a pending, unexpired invitation by the lowercase hex SHA-256 of its token.</summary>
    ValueTask<FoundInvitation?> FindInvitationAsync(string tokenHash, CancellationToken cancellationToken);

    /// <summary>Reads the pending, unexpired ownership transfer. Needs <see cref="OrganizationRole.Viewer"/>.</summary>
    ValueTask<ManagementResult<OwnershipTransfer>> GetTransferAsync(string userId, string organizationId, CancellationToken cancellationToken);

    /// <summary>Offers ownership to a member who is not an owner, replacing any pending offer. Needs <see cref="OrganizationRole.Owner"/>.</summary>
    ValueTask<ManagementResult<OwnershipTransfer>> StartTransferAsync(string userId, string organizationId, string memberId, CancellationToken cancellationToken);

    /// <summary>The nominee accepts: it becomes an owner and the owner who started the transfer becomes a member.</summary>
    ValueTask<ManagementResult<OrganizationMember>> ConfirmTransferAsync(string userId, string organizationId, CancellationToken cancellationToken);

    /// <summary>Cancels the pending transfer. Needs <see cref="OrganizationRole.Owner"/> or to be the nominee.</summary>
    ValueTask<ManagementError> CancelTransferAsync(string userId, string organizationId, CancellationToken cancellationToken);

    /// <summary>Reads the audit retention. Needs <see cref="OrganizationRole.Admin"/>.</summary>
    ValueTask<ManagementResult<AuditRetention>> GetAuditRetentionAsync(string userId, string organizationId, CancellationToken cancellationToken);

    /// <summary>Sets the audit retention (1 to 90 days, validated by the caller). Needs <see cref="OrganizationRole.Admin"/>.</summary>
    ValueTask<ManagementResult<AuditRetention>> SetAuditRetentionAsync(string userId, string organizationId, int days, CancellationToken cancellationToken);

    /// <summary>Lists audit events within the retention, newest first. Needs <see cref="OrganizationRole.Admin"/>.</summary>
    ValueTask<ManagementResult<ManagementPage<AuditEvent>>> ListAuditEventsAsync(
        string userId, string organizationId, string? after, int limit, CancellationToken cancellationToken);
}

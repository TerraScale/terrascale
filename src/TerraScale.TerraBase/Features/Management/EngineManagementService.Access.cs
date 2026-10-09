using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TerraBase.Engine;
using TerraBase.Errors;
using TerraBase.Hosting;
using TerraBase.Tenancy;

namespace TerraBase.Features.Management;

/// <summary>An invitation as stored: with hashes of its token, which is never stored or returned again.</summary>
/// <param name="Invitation">The invitation.</param>
/// <param name="SecretHash">
/// Lowercase hex SHA-256 of the secret part of a token TerraBase issued (<c>organization.invitation.secret</c>);
/// <see langword="null"/> when the client chose the token.
/// </param>
/// <param name="Order">Its key in the organization's <c>invitation_order</c> collection.</param>
/// <param name="TokenHash">Lowercase hex SHA-256 of the whole token; <see langword="null"/> for invitations from before token hashes.</param>
internal sealed record StoredInvitation(Invitation Invitation, string? SecretHash, string Order, string? TokenHash = null);

/// <summary>Where the invitation with a token hash is: the <c>invitation_tokens</c> index, which outlives its pending state.</summary>
/// <param name="OrganizationId">The invitation's organization.</param>
/// <param name="InvitationId">The invitation.</param>
internal sealed record InvitationToken(string OrganizationId, string InvitationId);

/// <summary>
/// The access part of the catalog. Members are keyed by user id; an <c>owners</c> index holds the owners so the
/// last-owner rule needs no scan. Every authorization check runs in the transaction that makes the change.
/// </summary>
/// <remarks>
/// Invitations and audit events are ordered by a per-organization sequence, a counter document incremented in
/// the same write transaction as the insert (writes are serialized). The sequence is the key of the collection
/// they are listed from, because the engine orders and pages by key, so their order survives restarts and does
/// not depend on the clock or on their ids, which are opaque. List cursors are those keys.
/// </remarks>
internal sealed partial class EngineManagementService : IAccessService
{
    // Documents of an organization's state collection.
    private const string OwnershipTransfers = "ownership_transfer";
    private const string AuditRetentions = "audit_retention";
    private const string AuditSequences = "audit_sequence";
    private const string InvitationSequences = "invitation_sequence";
    private const int DefaultAuditRetentionDays = 90;

    // Expired audit events deleted per recorded event; listing hides the rest until they are deleted.
    private const int AuditPurgeBatch = 100;

    private static readonly TimeSpan InvitationLifetime = TimeSpan.FromDays(7);
    private static readonly TimeSpan TransferLifetime = TimeSpan.FromDays(7);

    public ValueTask<ManagementResult<ManagementPage<OrganizationMember>>> ListMembersAsync(
        string userId, string organizationId, string? after, int limit, CancellationToken cancellationToken) =>
        ReadAsync<ManagementResult<ManagementPage<OrganizationMember>>>(async transaction =>
        {
            var actor = await AuthorizeMemberAsync(transaction, userId, organizationId, OrganizationRole.Viewer, cancellationToken).ConfigureAwait(false);
            return actor.Error != ManagementError.None
                ? actor.Error
                : await ListAsync(transaction, Members(organizationId), after, limit, TerraBaseJsonContext.Default.OrganizationMember, member => member.UserId, cancellationToken).ConfigureAwait(false);
        }, cancellationToken);

    public async ValueTask<ManagementResult<OrganizationMember>> ChangeRoleAsync(
        string userId, string organizationId, string memberId, OrganizationRole role, CancellationToken cancellationToken) =>
        await WriteAsync<ManagementResult<OrganizationMember>>(async transaction =>
        {
            var actor = await AuthorizeMemberAsync(transaction, userId, organizationId, OrganizationRole.Admin, cancellationToken).ConfigureAwait(false);
            if (actor.Value is null)
            {
                return actor.Error;
            }

            if (await GetMemberAsync(transaction, organizationId, memberId, cancellationToken).ConfigureAwait(false) is not { } member)
            {
                return ManagementError.NotFound;
            }

            if ((member.Role == OrganizationRole.Owner || role == OrganizationRole.Owner) && actor.Value.Role != OrganizationRole.Owner)
            {
                return ManagementError.Forbidden;
            }

            if (member.Role == OrganizationRole.Owner && role != OrganizationRole.Owner && await IsLastOwnerAsync(transaction, organizationId, cancellationToken).ConfigureAwait(false))
            {
                return ManagementError.LastOwner;
            }

            if (member.Role == role)
            {
                return member;
            }

            member = member with { Role = role };
            await PutMemberAsync(transaction, organizationId, member, cancellationToken).ConfigureAwait(false);
            await RecordAsync(transaction, organizationId, userId, "member.role_changed", memberId, RoleName(role), cancellationToken).ConfigureAwait(false);
            return member;
        }, cancellationToken).ConfigureAwait(false);

    public async ValueTask<AddMemberResult> AddMemberAsync(
        string userId, string organizationId, string memberId, string email, CancellationToken cancellationToken) =>
        await WriteAsync<AddMemberResult>(async transaction =>
        {
            var actor = await AuthorizeMemberAsync(transaction, userId, organizationId, OrganizationRole.Admin, cancellationToken).ConfigureAwait(false);
            if (actor.Error == ManagementError.Forbidden)
            {
                return new Forbidden("your role in the organization does not allow this", "forbidden_role");
            }

            if (actor.Value is null)
            {
                return new NotFound();
            }

            // Idempotent: an existing membership keeps its role and is not recorded again.
            if (await GetMemberAsync(transaction, organizationId, memberId, cancellationToken).ConfigureAwait(false) is { } existing)
            {
                return existing;
            }

            var member = new OrganizationMember(memberId, OrganizationRole.Member, time.GetUtcNow());
            await PutMemberAsync(transaction, organizationId, member, cancellationToken).ConfigureAwait(false);
            await RecordAsync(transaction, organizationId, userId, "member.added", memberId, email, cancellationToken).ConfigureAwait(false);
            return member;
        }, cancellationToken).ConfigureAwait(false);

    public async ValueTask<ManagementError> RemoveMemberAsync(string userId, string organizationId, string memberId, CancellationToken cancellationToken) =>
        await WriteAsync(async transaction =>
        {
            var actor = await AuthorizeMemberAsync(transaction, userId, organizationId, OrganizationRole.Admin, cancellationToken).ConfigureAwait(false);
            if (actor.Value is null)
            {
                return actor.Error;
            }

            if (await GetMemberAsync(transaction, organizationId, memberId, cancellationToken).ConfigureAwait(false) is not { } member)
            {
                return ManagementError.NotFound;
            }

            if (member.Role == OrganizationRole.Owner)
            {
                if (actor.Value.Role != OrganizationRole.Owner)
                {
                    return ManagementError.Forbidden;
                }

                if (await IsLastOwnerAsync(transaction, organizationId, cancellationToken).ConfigureAwait(false))
                {
                    return ManagementError.LastOwner;
                }
            }

            await DeleteMemberAsync(transaction, organizationId, memberId, cancellationToken).ConfigureAwait(false);
            await RecordAsync(transaction, organizationId, userId, "member.removed", memberId, null, cancellationToken).ConfigureAwait(false);
            return ManagementError.None;
        }, cancellationToken).ConfigureAwait(false);

    public async ValueTask<ManagementError> LeaveAsync(string userId, string organizationId, CancellationToken cancellationToken) =>
        await WriteAsync(async transaction =>
        {
            var actor = await AuthorizeMemberAsync(transaction, userId, organizationId, OrganizationRole.Viewer, cancellationToken).ConfigureAwait(false);
            if (actor.Value is null)
            {
                return actor.Error;
            }

            if (actor.Value.Role == OrganizationRole.Owner && await IsLastOwnerAsync(transaction, organizationId, cancellationToken).ConfigureAwait(false))
            {
                return ManagementError.LastOwner;
            }

            await DeleteMemberAsync(transaction, organizationId, userId, cancellationToken).ConfigureAwait(false);
            await RecordAsync(transaction, organizationId, userId, "member.left", null, null, cancellationToken).ConfigureAwait(false);
            return ManagementError.None;
        }, cancellationToken).ConfigureAwait(false);

    public async ValueTask<ManagementResult<CreatedInvitation>> InviteAsync(
        string userId, string organizationId, string email, OrganizationRole role, CancellationToken cancellationToken)
    {
        var secret = Base64Url(RandomNumberGenerator.GetBytes(32));
        var id = NewId();
        // The token names its invitation, so accepting it needs no global token index.
        var token = $"{organizationId}.{id}.{secret}";
        var created = await InviteAsync(userId, organizationId, id, email, role, Hash(token), Hash(secret), cancellationToken).ConfigureAwait(false);
        return created.Value is { } invitation ? new CreatedInvitation(invitation, token) : created.Error;
    }

    // Creates a pending invitation whose token has tokenHash; secretHash is set only for tokens TerraBase issued.
    private ValueTask<ManagementResult<Invitation>> InviteAsync(
        string userId, string organizationId, string id, string email, OrganizationRole role, string tokenHash, string? secretHash, CancellationToken cancellationToken) =>
        WriteAsync<ManagementResult<Invitation>>(async transaction =>
        {
            var actor = await AuthorizeMemberAsync(transaction, userId, organizationId, OrganizationRole.Admin, cancellationToken).ConfigureAwait(false);
            if (actor.Value is null)
            {
                return actor.Error;
            }

            if (role == OrganizationRole.Owner && actor.Value.Role != OrganizationRole.Owner)
            {
                return ManagementError.Forbidden;
            }

            var now = time.GetUtcNow();
            if (await PendingInvitationAsync(transaction, organizationId, email, cancellationToken).ConfigureAwait(false) is { } pending
                && pending.Invitation.ExpiresAt > now)
            {
                return ManagementError.InvitationPending;
            }

            if (await transaction.GetAsync(InvitationTokens, tokenHash, cancellationToken).ConfigureAwait(false) is not null)
            {
                return ManagementError.TokenTaken;
            }

            // An expired invitation for the email stays in the history; the new one takes its email slot.
            var invitation = new Invitation(id, email, role, userId, now, now + InvitationLifetime);
            var order = AscendingKey(await NextSequenceAsync(transaction, State(organizationId), InvitationSequences, cancellationToken).ConfigureAwait(false));
            await PutAsync(transaction, Invitations(organizationId), invitation.Id, new StoredInvitation(invitation, secretHash, order, tokenHash), TerraBaseJsonContext.Default.StoredInvitation, cancellationToken)
                .ConfigureAwait(false);
            await PutAsync(transaction, InvitationOrder(organizationId), order, invitation.Id, Id, cancellationToken).ConfigureAwait(false);
            await PutAsync(transaction, InvitationEmails(organizationId), Key(email), invitation.Id, Id, cancellationToken).ConfigureAwait(false);
            await ReserveTokenAsync(transaction, organizationId, invitation.Id, tokenHash, cancellationToken).ConfigureAwait(false);
            await RecordAsync(transaction, organizationId, userId, "invitation.created", email, RoleName(role), cancellationToken).ConfigureAwait(false);
            return invitation;
        }, cancellationToken);

    // Gives a pending invitation, expired or not, a new token, creation time and expiry; the old token stops working.
    private ValueTask<ManagementResult<Invitation>> ResendInvitationAsync(
        string userId, string organizationId, string invitationId, string tokenHash, CancellationToken cancellationToken) =>
        WriteAsync<ManagementResult<Invitation>>(async transaction =>
        {
            var actor = await AuthorizeMemberAsync(transaction, userId, organizationId, OrganizationRole.Admin, cancellationToken).ConfigureAwait(false);
            if (actor.Value is null)
            {
                return actor.Error;
            }

            if (await GetAsync(transaction, Invitations(organizationId), invitationId, TerraBaseJsonContext.Default.StoredInvitation, cancellationToken).ConfigureAwait(false) is not { } stored)
            {
                return ManagementError.NotFound;
            }

            // Renewing an owner invitation hands out ownership, which only owners may offer.
            if (stored.Invitation.Role == OrganizationRole.Owner && actor.Value.Role != OrganizationRole.Owner)
            {
                return ManagementError.Forbidden;
            }

            if (stored.Invitation.Status != InvitationStatus.Pending)
            {
                return ManagementError.InvitationClosed;
            }

            // A newer invitation for the email took its slot after it expired.
            if (await GetAsync(transaction, InvitationEmails(organizationId), Key(stored.Invitation.Email), Id, cancellationToken).ConfigureAwait(false) != invitationId)
            {
                return ManagementError.InvitationPending;
            }

            if (await transaction.GetAsync(InvitationTokens, tokenHash, cancellationToken).ConfigureAwait(false) is not null)
            {
                return ManagementError.TokenTaken;
            }

            var now = time.GetUtcNow();
            var invitation = stored.Invitation with { CreatedAt = now, ExpiresAt = now + InvitationLifetime };
            await PutAsync(transaction, Invitations(organizationId), invitationId, stored with { Invitation = invitation, SecretHash = null, TokenHash = tokenHash }, TerraBaseJsonContext.Default.StoredInvitation, cancellationToken)
                .ConfigureAwait(false);
            // The previous hash stays reserved: it no longer matches the invitation, so it redeems nothing, and no
            // other invitation can take it.
            await ReserveTokenAsync(transaction, organizationId, invitationId, tokenHash, cancellationToken).ConfigureAwait(false);
            await RecordAsync(transaction, organizationId, userId, "invitation.resent", invitation.Email, RoleName(invitation.Role), cancellationToken).ConfigureAwait(false);
            return invitation;
        }, cancellationToken);

    public ValueTask<ManagementResult<ManagementPage<Invitation>>> ListInvitationsAsync(
        string userId, string organizationId, string? after, int limit, CancellationToken cancellationToken) =>
        ReadAsync<ManagementResult<ManagementPage<Invitation>>>(async transaction =>
        {
            var actor = await AuthorizeMemberAsync(transaction, userId, organizationId, OrganizationRole.Admin, cancellationToken).ConfigureAwait(false);
            if (actor.Error != ManagementError.None)
            {
                return actor.Error;
            }

            var page = await transaction.ListAsync(InvitationOrder(organizationId), after, limit, MaxPageBytes, cancellationToken).ConfigureAwait(false);
            var invitations = new List<Invitation>(page.Documents.Count);
            foreach (var entry in page.Documents)
            {
                // Written and deleted with the invitation: always present.
                var stored = await GetAsync(transaction, Invitations(organizationId), JsonSerializer.Deserialize(entry.Json.Span, Id)!, TerraBaseJsonContext.Default.StoredInvitation, cancellationToken)
                    .ConfigureAwait(false) ?? throw new InvalidOperationException("the invitation order names a missing invitation");
                invitations.Add(stored.Invitation);
            }

            return new ManagementPage<Invitation>(invitations, page.HasMore ? page.Documents[^1].Id : null);
        }, cancellationToken);

    public async ValueTask<ManagementError> RevokeInvitationAsync(string userId, string organizationId, string invitationId, CancellationToken cancellationToken) =>
        await WriteAsync(async transaction =>
        {
            var actor = await AuthorizeMemberAsync(transaction, userId, organizationId, OrganizationRole.Admin, cancellationToken).ConfigureAwait(false);
            if (actor.Value is null)
            {
                return actor.Error;
            }

            if (await GetAsync(transaction, Invitations(organizationId), invitationId, TerraBaseJsonContext.Default.StoredInvitation, cancellationToken).ConfigureAwait(false) is not { } stored)
            {
                return ManagementError.NotFound;
            }

            if (stored.Invitation.Status != InvitationStatus.Pending)
            {
                return ManagementError.InvitationClosed;
            }

            await CloseInvitationAsync(transaction, organizationId, stored, InvitationStatus.Revoked, cancellationToken).ConfigureAwait(false);
            await RecordAsync(transaction, organizationId, userId, "invitation.revoked", stored.Invitation.Email, null, cancellationToken).ConfigureAwait(false);
            return ManagementError.None;
        }, cancellationToken).ConfigureAwait(false);

    public async ValueTask<ManagementResult<OrganizationMember>> AcceptInvitationAsync(string userId, string email, string token, CancellationToken cancellationToken)
    {
        if (token.Split('.') is not [var organizationId, var invitationId, var secret])
        {
            return ManagementError.NotFound;
        }

        var accepted = await WriteAsync<ManagementResult<Membership>>(async transaction =>
            await GetAsync(transaction, Invitations(organizationId), invitationId, TerraBaseJsonContext.Default.StoredInvitation, cancellationToken).ConfigureAwait(false) is { SecretHash: { } secretHash } stored
            && Matches(secretHash, Hash(secret))
                ? await AcceptAsync(transaction, userId, email, organizationId, stored, cancellationToken).ConfigureAwait(false)
                : ManagementError.NotFound,
            cancellationToken).ConfigureAwait(false);
        return accepted.Value is { } membership ? membership.Member : accepted.Error;
    }

    public ValueTask<FoundInvitation?> FindInvitationAsync(string tokenHash, CancellationToken cancellationToken) =>
        ReadAsync<FoundInvitation?>(async transaction =>
            await TokenInvitationAsync(transaction, tokenHash, cancellationToken).ConfigureAwait(false) is (var organizationId, var stored)
            && stored.Invitation.Status == InvitationStatus.Pending
            && stored.Invitation.ExpiresAt > time.GetUtcNow()
            && await GetAsync(transaction, Organizations, organizationId, TerraBaseJsonContext.Default.Organization, cancellationToken).ConfigureAwait(false) is { } organization
                ? new FoundInvitation(new OrganizationIdentity(organization.Id, organization.Name), stored.Invitation)
                : null,
            cancellationToken);

    private ValueTask<ManagementResult<Membership>> AcceptInvitationByHashAsync(string userId, string email, string tokenHash, CancellationToken cancellationToken) =>
        WriteAsync<ManagementResult<Membership>>(async transaction =>
            await TokenInvitationAsync(transaction, tokenHash, cancellationToken).ConfigureAwait(false) is (var organizationId, var stored)
                ? await AcceptAsync(transaction, userId, email, organizationId, stored, cancellationToken).ConfigureAwait(false)
                : ManagementError.NotFound,
            cancellationToken);

    private ValueTask<ManagementResult<Invitation>> DeclineInvitationAsync(string userId, string email, string tokenHash, CancellationToken cancellationToken) =>
        WriteAsync<ManagementResult<Invitation>>(async transaction =>
        {
            if (await TokenInvitationAsync(transaction, tokenHash, cancellationToken).ConfigureAwait(false) is not (var organizationId, var stored))
            {
                return ManagementError.NotFound;
            }

            var check = Redeemable(stored.Invitation, email);
            if (check != ManagementError.None)
            {
                return check;
            }

            var invitation = await CloseInvitationAsync(transaction, organizationId, stored, InvitationStatus.Declined, cancellationToken).ConfigureAwait(false);
            await RecordAsync(transaction, organizationId, userId, "invitation.declined", invitation.Email, null, cancellationToken).ConfigureAwait(false);
            return invitation;
        }, cancellationToken);

    // Makes the user a member through a pending invitation whose token was already verified.
    private async ValueTask<ManagementResult<Membership>> AcceptAsync(
        IStorageTransaction transaction, string userId, string email, string organizationId, StoredInvitation stored, CancellationToken cancellationToken)
    {
        if (await GetAsync(transaction, Organizations, organizationId, TerraBaseJsonContext.Default.Organization, cancellationToken).ConfigureAwait(false) is null)
        {
            return ManagementError.NotFound;
        }

        var check = Redeemable(stored.Invitation, email);
        if (check != ManagementError.None)
        {
            return check;
        }

        if (await GetMemberAsync(transaction, organizationId, userId, cancellationToken).ConfigureAwait(false) is not null)
        {
            return ManagementError.AlreadyMember;
        }

        var invitation = stored.Invitation;
        var member = new OrganizationMember(userId, invitation.Role, time.GetUtcNow());
        await PutMemberAsync(transaction, organizationId, member, cancellationToken).ConfigureAwait(false);
        await CloseInvitationAsync(transaction, organizationId, stored, InvitationStatus.Accepted, cancellationToken).ConfigureAwait(false);
        await RecordAsync(transaction, organizationId, userId, "invitation.accepted", invitation.Email, RoleName(invitation.Role), cancellationToken).ConfigureAwait(false);
        return new Membership(organizationId, member);
    }

    // Whether the holder of an invitation's token, whose verified email is email, may still accept or decline it.
    private ManagementError Redeemable(Invitation invitation, string email) =>
        invitation.Status != InvitationStatus.Pending ? ManagementError.InvitationClosed
        : !string.Equals(invitation.Email, email, StringComparison.OrdinalIgnoreCase) ? ManagementError.Forbidden
        : invitation.ExpiresAt <= time.GetUtcNow() ? ManagementError.Expired
        : ManagementError.None;

    // The invitation a token hash names, with its organization, when the hash matches it.
    private static async ValueTask<(string OrganizationId, StoredInvitation Stored)?> TokenInvitationAsync(
        IStorageTransaction transaction, string tokenHash, CancellationToken cancellationToken) =>
        await GetAsync(transaction, InvitationTokens, tokenHash, TerraBaseJsonContext.Default.InvitationToken, cancellationToken).ConfigureAwait(false) is { } entry
        && await GetAsync(transaction, Invitations(entry.OrganizationId), entry.InvitationId, TerraBaseJsonContext.Default.StoredInvitation, cancellationToken).ConfigureAwait(false) is { TokenHash: { } stored } invitation
        && Matches(stored, tokenHash)
            ? (entry.OrganizationId, invitation)
            : null;

    public ValueTask<ManagementResult<OwnershipTransfer>> GetTransferAsync(string userId, string organizationId, CancellationToken cancellationToken) =>
        ReadAsync<ManagementResult<OwnershipTransfer>>(async transaction =>
        {
            var actor = await AuthorizeMemberAsync(transaction, userId, organizationId, OrganizationRole.Viewer, cancellationToken).ConfigureAwait(false);
            if (actor.Error != ManagementError.None)
            {
                return actor.Error;
            }

            return await GetTransferAsync(transaction, organizationId, cancellationToken).ConfigureAwait(false) is { } transfer
                ? transfer
                : ManagementError.NotFound;
        }, cancellationToken);

    public async ValueTask<ManagementResult<OwnershipTransfer>> StartTransferAsync(
        string userId, string organizationId, string memberId, CancellationToken cancellationToken) =>
        await WriteAsync<ManagementResult<OwnershipTransfer>>(async transaction =>
        {
            var actor = await AuthorizeMemberAsync(transaction, userId, organizationId, OrganizationRole.Owner, cancellationToken).ConfigureAwait(false);
            if (actor.Value is null)
            {
                return actor.Error;
            }

            if (await GetMemberAsync(transaction, organizationId, memberId, cancellationToken).ConfigureAwait(false) is not { } member)
            {
                return ManagementError.NotFound;
            }

            if (member.Role == OrganizationRole.Owner)
            {
                return ManagementError.AlreadyOwner;
            }

            var now = time.GetUtcNow();
            var transfer = new OwnershipTransfer(userId, memberId, now, now + TransferLifetime);
            await PutAsync(transaction, State(organizationId), OwnershipTransfers, transfer, TerraBaseJsonContext.Default.OwnershipTransfer, cancellationToken).ConfigureAwait(false);
            await RecordAsync(transaction, organizationId, userId, "ownership.transfer_started", memberId, null, cancellationToken).ConfigureAwait(false);
            return transfer;
        }, cancellationToken).ConfigureAwait(false);

    public async ValueTask<ManagementResult<OrganizationMember>> ConfirmTransferAsync(string userId, string organizationId, CancellationToken cancellationToken) =>
        await WriteAsync<ManagementResult<OrganizationMember>>(async transaction =>
        {
            var actor = await AuthorizeMemberAsync(transaction, userId, organizationId, OrganizationRole.Viewer, cancellationToken).ConfigureAwait(false);
            if (actor.Value is null)
            {
                return actor.Error;
            }

            if (await GetTransferAsync(transaction, organizationId, cancellationToken).ConfigureAwait(false) is not { } transfer)
            {
                return ManagementError.NotFound;
            }

            if (transfer.ToUserId != userId)
            {
                return ManagementError.Forbidden;
            }

            // The offer lapses when the owner who made it is no longer an owner (losing the role already dropped it).
            if (await GetMemberAsync(transaction, organizationId, transfer.FromUserId, cancellationToken).ConfigureAwait(false) is not { Role: OrganizationRole.Owner } starter)
            {
                return ManagementError.NotFound;
            }

            await transaction.DeleteAsync(State(organizationId), OwnershipTransfers, cancellationToken).ConfigureAwait(false);

            var owner = actor.Value with { Role = OrganizationRole.Owner };
            await PutMemberAsync(transaction, organizationId, owner, cancellationToken).ConfigureAwait(false);
            await PutMemberAsync(transaction, organizationId, starter with { Role = OrganizationRole.Member }, cancellationToken).ConfigureAwait(false);
            await RecordAsync(transaction, organizationId, userId, "ownership.transfer_confirmed", starter.UserId, null, cancellationToken).ConfigureAwait(false);
            return owner;
        }, cancellationToken).ConfigureAwait(false);

    public async ValueTask<ManagementError> CancelTransferAsync(string userId, string organizationId, CancellationToken cancellationToken) =>
        await WriteAsync(async transaction =>
        {
            var actor = await AuthorizeMemberAsync(transaction, userId, organizationId, OrganizationRole.Viewer, cancellationToken).ConfigureAwait(false);
            if (actor.Value is null)
            {
                return actor.Error;
            }

            if (await GetTransferAsync(transaction, organizationId, cancellationToken).ConfigureAwait(false) is not { } transfer)
            {
                return ManagementError.NotFound;
            }

            if (actor.Value.Role != OrganizationRole.Owner && transfer.ToUserId != userId)
            {
                return ManagementError.Forbidden;
            }

            await transaction.DeleteAsync(State(organizationId), OwnershipTransfers, cancellationToken).ConfigureAwait(false);
            await RecordAsync(transaction, organizationId, userId, "ownership.transfer_cancelled", transfer.ToUserId, null, cancellationToken).ConfigureAwait(false);
            return ManagementError.None;
        }, cancellationToken).ConfigureAwait(false);

    public ValueTask<ManagementResult<AuditRetention>> GetAuditRetentionAsync(string userId, string organizationId, CancellationToken cancellationToken) =>
        ReadAsync<ManagementResult<AuditRetention>>(async transaction =>
        {
            var actor = await AuthorizeMemberAsync(transaction, userId, organizationId, OrganizationRole.Admin, cancellationToken).ConfigureAwait(false);
            return actor.Error != ManagementError.None
                ? actor.Error
                : await GetAuditRetentionAsync(transaction, organizationId, cancellationToken).ConfigureAwait(false);
        }, cancellationToken);

    public async ValueTask<ManagementResult<AuditRetention>> SetAuditRetentionAsync(
        string userId, string organizationId, int days, CancellationToken cancellationToken) =>
        await WriteAsync<ManagementResult<AuditRetention>>(async transaction =>
        {
            var actor = await AuthorizeMemberAsync(transaction, userId, organizationId, OrganizationRole.Admin, cancellationToken).ConfigureAwait(false);
            if (actor.Value is null)
            {
                return actor.Error;
            }

            var retention = new AuditRetention(days);
            await PutAsync(transaction, State(organizationId), AuditRetentions, retention, TerraBaseJsonContext.Default.AuditRetention, cancellationToken).ConfigureAwait(false);
            await RecordAsync(transaction, organizationId, userId, "settings.audit_retention_updated", null, days.ToString(CultureInfo.InvariantCulture), cancellationToken).ConfigureAwait(false);
            return retention;
        }, cancellationToken).ConfigureAwait(false);

    public ValueTask<ManagementResult<ManagementPage<AuditEvent>>> ListAuditEventsAsync(
        string userId, string organizationId, string? after, int limit, CancellationToken cancellationToken) =>
        ReadAsync<ManagementResult<ManagementPage<AuditEvent>>>(async transaction =>
        {
            var actor = await AuthorizeMemberAsync(transaction, userId, organizationId, OrganizationRole.Admin, cancellationToken).ConfigureAwait(false);
            if (actor.Error != ManagementError.None)
            {
                return actor.Error;
            }

            // The order follows the sequence, and a clock step back can put an older occurredAt after a newer one,
            // so retention is checked per event; expired events not purged yet are skipped, not paged over.
            var cutoff = await AuditCutoffAsync(transaction, organizationId, cancellationToken).ConfigureAwait(false);
            var page = await transaction.ListAsync(AuditOrder(organizationId), after, limit, MaxPageBytes, cancellationToken).ConfigureAwait(false);
            var events = new List<AuditEvent>(page.Documents.Count);
            foreach (var entry in page.Documents)
            {
                var eventId = JsonSerializer.Deserialize(entry.Json.Span, Id)!;
                events.Add(await GetAsync(transaction, Audit(organizationId), eventId, TerraBaseJsonContext.Default.AuditEvent, cancellationToken)
                    .ConfigureAwait(false) ?? throw new InvalidDataException("audit order names a missing event"));
            }
            return new ManagementPage<AuditEvent>([.. events.Where(auditEvent => auditEvent.OccurredAt >= cutoff)], page.HasMore ? page.Documents[^1].Id : null);
        }, cancellationToken);

    // Database operations may use a signed project grant; access operations always require persisted membership.
    private static ValueTask<ManagementResult<OrganizationMember>> AuthorizeAsync(
        IStorageTransaction transaction, string userId, string organizationId, OrganizationRole minimum, CancellationToken cancellationToken) =>
        ProjectActor(userId, organizationId) && minimum <= OrganizationRole.Member
            ? ValueTask.FromResult<ManagementResult<OrganizationMember>>(new OrganizationMember(userId, OrganizationRole.Member, DateTimeOffset.MinValue))
            : AuthorizeMemberAsync(transaction, userId, organizationId, minimum, cancellationToken);

    // The acting member, or why it may not act: NotFound for a non-member, exactly like a missing organization
    // (members are deleted with their organization), Forbidden for a role below minimum.
    private static async ValueTask<ManagementResult<OrganizationMember>> AuthorizeMemberAsync(
        IStorageTransaction transaction, string userId, string organizationId, OrganizationRole minimum, CancellationToken cancellationToken)
    {
        if (await GetMemberAsync(transaction, organizationId, userId, cancellationToken).ConfigureAwait(false) is not { } actor)
        {
            return ManagementError.NotFound;
        }

        return actor.Role >= minimum ? actor : ManagementError.Forbidden;
    }

    private static ValueTask<OrganizationMember?> GetMemberAsync(
        IStorageTransaction transaction, string organizationId, string userId, CancellationToken cancellationToken) =>
        GetAsync(transaction, Members(organizationId), userId, TerraBaseJsonContext.Default.OrganizationMember, cancellationToken);

    // Writes the membership, the user's organization index and the owners index together, and advances the user's account version.
    private static async ValueTask PutMemberAsync(
        IStorageTransaction transaction, string organizationId, OrganizationMember member, CancellationToken cancellationToken)
    {
        await TouchAccountAsync(transaction, member.UserId, cancellationToken).ConfigureAwait(false);
        await PutAsync(transaction, Members(organizationId), member.UserId, member, TerraBaseJsonContext.Default.OrganizationMember, cancellationToken).ConfigureAwait(false);
        await PutAsync(transaction, UserOrganizations(member.UserId), organizationId, organizationId, Id, cancellationToken).ConfigureAwait(false);
        if (member.Role == OrganizationRole.Owner)
        {
            await PutAsync(transaction, Owners(organizationId), member.UserId, member.UserId, Id, cancellationToken).ConfigureAwait(false);
        }
        else if (await transaction.DeleteAsync(Owners(organizationId), member.UserId, cancellationToken).ConfigureAwait(false))
        {
            await DropTransferAsync(transaction, organizationId, member.UserId, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async ValueTask DeleteMemberAsync(IStorageTransaction transaction, string organizationId, string userId, CancellationToken cancellationToken)
    {
        await TouchAccountAsync(transaction, userId, cancellationToken).ConfigureAwait(false);
        await transaction.DeleteAsync(Members(organizationId), userId, cancellationToken).ConfigureAwait(false);
        await transaction.DeleteAsync(UserOrganizations(userId), organizationId, cancellationToken).ConfigureAwait(false);
        await transaction.DeleteAsync(Owners(organizationId), userId, cancellationToken).ConfigureAwait(false);
        await DropTransferAsync(transaction, organizationId, userId, cancellationToken).ConfigureAwait(false);
    }

    // An offer is void once its starter stops being an owner or either side leaves, even if that user
    // later regains the role or rejoins: confirming needs a new offer.
    private static async ValueTask DropTransferAsync(IStorageTransaction transaction, string organizationId, string userId, CancellationToken cancellationToken)
    {
        if (await GetAsync(transaction, State(organizationId), OwnershipTransfers, TerraBaseJsonContext.Default.OwnershipTransfer, cancellationToken).ConfigureAwait(false) is { } transfer
            && (transfer.FromUserId == userId || transfer.ToUserId == userId))
        {
            await transaction.DeleteAsync(State(organizationId), OwnershipTransfers, cancellationToken).ConfigureAwait(false);
        }
    }

    // Called only for an owner, so the index holds at least one entry.
    private static async ValueTask<bool> IsLastOwnerAsync(IStorageTransaction transaction, string organizationId, CancellationToken cancellationToken) =>
        (await transaction.ListAsync(Owners(organizationId), afterId: null, limit: 2, MaxPageBytes, cancellationToken).ConfigureAwait(false)).Documents.Count < 2;

    // Ends a pending invitation: it keeps its place in the history with its new status and frees its email slot. Its
    // token index entry stays, so redeeming it again is InvitationClosed rather than NotFound.
    private static async ValueTask<Invitation> CloseInvitationAsync(
        IStorageTransaction transaction, string organizationId, StoredInvitation stored, InvitationStatus status, CancellationToken cancellationToken)
    {
        var invitation = stored.Invitation with { Status = status };
        await PutAsync(transaction, Invitations(organizationId), invitation.Id, stored with { Invitation = invitation }, TerraBaseJsonContext.Default.StoredInvitation, cancellationToken)
            .ConfigureAwait(false);
        if (await GetAsync(transaction, InvitationEmails(organizationId), Key(invitation.Email), Id, cancellationToken).ConfigureAwait(false) == invitation.Id)
        {
            await transaction.DeleteAsync(InvitationEmails(organizationId), Key(invitation.Email), cancellationToken).ConfigureAwait(false);
        }

        return invitation;
    }

    // The invitation holding the email's slot: the latest one for it, while it is pending (expired or not).
    private static async ValueTask<StoredInvitation?> PendingInvitationAsync(
        IStorageTransaction transaction, string organizationId, string email, CancellationToken cancellationToken) =>
        await GetAsync(transaction, InvitationEmails(organizationId), Key(email), Id, cancellationToken).ConfigureAwait(false) is { } id
            ? await GetAsync(transaction, Invitations(organizationId), id, TerraBaseJsonContext.Default.StoredInvitation, cancellationToken).ConfigureAwait(false)
            : null;

    // Indexes a token hash for good: every hash an invitation ever had stays taken until the organization is deleted.
    private static async ValueTask ReserveTokenAsync(
        IStorageTransaction transaction, string organizationId, string invitationId, string tokenHash, CancellationToken cancellationToken)
    {
        await PutAsync(transaction, InvitationTokens, tokenHash, new InvitationToken(organizationId, invitationId), TerraBaseJsonContext.Default.InvitationToken, cancellationToken)
            .ConfigureAwait(false);
        await PutAsync(transaction, OrganizationTokens(organizationId), tokenHash, invitationId, Id, cancellationToken).ConfigureAwait(false);
    }

    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    // Constant time over the two hex hashes; anything that is not a SHA-256 in hex never matches.
    private static bool Matches(string storedHash, string presentedHash) =>
        storedHash.Length == presentedHash.Length
        && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(storedHash), Encoding.ASCII.GetBytes(presentedHash));

    // An expired transfer is treated as none.
    private async ValueTask<OwnershipTransfer?> GetTransferAsync(IStorageTransaction transaction, string organizationId, CancellationToken cancellationToken) =>
        await GetAsync(transaction, State(organizationId), OwnershipTransfers, TerraBaseJsonContext.Default.OwnershipTransfer, cancellationToken).ConfigureAwait(false) is { } transfer
        && transfer.ExpiresAt > time.GetUtcNow()
            ? transfer
            : null;

    private static async ValueTask<AuditRetention> GetAuditRetentionAsync(IStorageTransaction transaction, string organizationId, CancellationToken cancellationToken) =>
        await GetAsync(transaction, State(organizationId), AuditRetentions, TerraBaseJsonContext.Default.AuditRetention, cancellationToken).ConfigureAwait(false)
        ?? new AuditRetention(DefaultAuditRetentionDays);

    // Events that occurred before this time are past the retention.
    private async ValueTask<DateTimeOffset> AuditCutoffAsync(IStorageTransaction transaction, string organizationId, CancellationToken cancellationToken)
    {
        var retention = await GetAuditRetentionAsync(transaction, organizationId, cancellationToken).ConfigureAwait(false);
        return time.GetUtcNow() - TimeSpan.FromDays(retention.Days);
    }

    // Records an event under the next sequence, newest first, and deletes a batch of expired ones. The
    // audit_expiry index orders the same events by occurredAt ticks, so purging takes exactly the events that
    // occurred before the cutoff, oldest first, whatever order the clock gave them.
    // Every recorded change is a change to the organization, so it also advances the organization's version.
    private async ValueTask RecordAsync(
        IStorageTransaction transaction, string organizationId, string userId, string action, string? target, string? detail, CancellationToken cancellationToken)
    {
        await TouchOrganizationAsync(transaction, organizationId, cancellationToken).ConfigureAwait(false);
        var now = time.GetUtcNow();
        var sequence = await NextSequenceAsync(transaction, State(organizationId), AuditSequences, cancellationToken).ConfigureAwait(false);
        var key = DescendingKey(sequence);
        var auditEvent = new AuditEvent(NewId(), userId, action, target, detail, now);
        await PutAsync(transaction, Audit(organizationId), auditEvent.Id, auditEvent, TerraBaseJsonContext.Default.AuditEvent, cancellationToken).ConfigureAwait(false);
        await PutAsync(transaction, AuditOrder(organizationId), key, auditEvent.Id, Id, cancellationToken).ConfigureAwait(false);
        await PutAsync(transaction, AuditExpiry(organizationId), TicksKey(now) + AscendingKey(sequence), key, Id, cancellationToken).ConfigureAwait(false);

        // An entry sorts before the bare prefix of the cutoff exactly when its ticks are smaller.
        var cutoff = TicksKey(await AuditCutoffAsync(transaction, organizationId, cancellationToken).ConfigureAwait(false));
        var oldest = await transaction.ListAsync(AuditExpiry(organizationId), afterId: null, AuditPurgeBatch, MaxPageBytes, cancellationToken).ConfigureAwait(false);
        foreach (var entry in oldest.Documents.TakeWhile(entry => string.CompareOrdinal(entry.Id, cutoff) < 0))
        {
            var order = JsonSerializer.Deserialize(entry.Json.Span, Id)!;
            var eventId = await GetAsync(transaction, AuditOrder(organizationId), order, Id, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("audit expiry names a missing order entry");
            await transaction.DeleteAsync(Audit(organizationId), eventId, cancellationToken).ConfigureAwait(false);
            await transaction.DeleteAsync(AuditOrder(organizationId), order, cancellationToken).ConfigureAwait(false);
            await transaction.DeleteAsync(AuditExpiry(organizationId), entry.Id, cancellationToken).ConfigureAwait(false);
        }
    }

    // The next value of a counter document, incremented in the caller's write transaction.
    internal static async ValueTask<long> NextSequenceAsync(IStorageTransaction transaction, string collection, string counter, CancellationToken cancellationToken)
    {
        var next = await transaction.GetAsync(collection, counter, cancellationToken).ConfigureAwait(false) is { } current
            ? JsonSerializer.Deserialize(current.Json.Span, TerraBaseJsonContext.Default.Int64) + 1
            : 0;
        await PutAsync(transaction, collection, counter, next, TerraBaseJsonContext.Default.Int64, cancellationToken).ConfigureAwait(false);
        return next;
    }

    // Removes what a deleted organization keeps besides its members (deleted by DeleteOrganizationAsync), page by page.
    private static async ValueTask DeleteAccessAsync(IStorageTransaction transaction, string organizationId, CancellationToken cancellationToken)
    {
        // The token index is global; the organization's copy of its keys says which entries go with it.
        string? after = null;
        DocumentPage tokens;
        do
        {
            tokens = await transaction.ListAsync(OrganizationTokens(organizationId), after, AuditPurgeBatch, MaxPageBytes, cancellationToken).ConfigureAwait(false);
            foreach (var token in tokens.Documents)
            {
                await transaction.DeleteAsync(InvitationTokens, token.Id, cancellationToken).ConfigureAwait(false);
            }

            after = tokens.Documents.Count > 0 ? tokens.Documents[^1].Id : after;
        }
        while (tokens.HasMore);

        foreach (var collection in (string[])[
            Owners(organizationId), Invitations(organizationId), InvitationOrder(organizationId), InvitationEmails(organizationId),
            Audit(organizationId), AuditOrder(organizationId), AuditExpiry(organizationId), State(organizationId), OrganizationTokens(organizationId)])
        {
            DocumentPage page;
            do
            {
                page = await transaction.ListAsync(collection, afterId: null, AuditPurgeBatch, MaxPageBytes, cancellationToken).ConfigureAwait(false);
                foreach (var document in page.Documents)
                {
                    await transaction.DeleteAsync(collection, document.Id, cancellationToken).ConfigureAwait(false);
                }
            }
            while (page.HasMore);
        }

    }

    // Keys sort by ordinal comparison, so numbers are written with a fixed width of 19 digits (any long).
    internal static string AscendingKey(long sequence) => sequence.ToString("D19", CultureInfo.InvariantCulture);

    // The engine lists keys in ascending order; counting down from long.MaxValue lists the newest first.
    private static string DescendingKey(long sequence) => AscendingKey(long.MaxValue - sequence);

    internal static string TicksKey(DateTimeOffset time) => AscendingKey(time.UtcTicks);

    private static string PlanName(PlanTier plan) => plan switch
    {
        PlanTier.Enterprise => "enterprise",
        PlanTier.Pro => "pro",
        _ => "hobby",
    };

    private static string RoleName(OrganizationRole role) => role switch
    {
        OrganizationRole.Owner => "owner",
        OrganizationRole.Admin => "admin",
        OrganizationRole.Member => "member",
        _ => "viewer",
    };

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string Owners(string organizationId) => $"organizations/{organizationId}/owners";

    private static string Invitations(string organizationId) => $"organizations/{organizationId}/invitations";

    // Global: token hash to invitation. Removed with the organization, through its copy of the keys below.
    private const string InvitationTokens = "invitation_tokens";

    private static string OrganizationTokens(string organizationId) => $"organizations/{organizationId}/invitation_tokens";

    private static string InvitationOrder(string organizationId) => $"organizations/{organizationId}/invitation_order";

    private static string InvitationEmails(string organizationId) => $"organizations/{organizationId}/invitation_emails";

    private static string Audit(string organizationId) => $"organizations/{organizationId}/audit";

    private static string AuditOrder(string organizationId) => $"organizations/{organizationId}/audit_order";

    // Single documents of an organization: its pending transfer, audit retention and counters.
    internal static string State(string organizationId) => $"organizations/{organizationId}/state";

    private static string AuditExpiry(string organizationId) => $"organizations/{organizationId}/audit_expiry";
}

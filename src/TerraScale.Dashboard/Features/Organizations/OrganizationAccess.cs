using System.Net.Mail;
using TerraScale.Dashboard.Features.Accounts;
using TerraScale.Dashboard.Features.Auth;

namespace TerraScale.Dashboard.Features.Organizations;

public sealed record OrganizationAccessView(IReadOnlyList<MemberRecord> Members, IReadOnlyList<InvitationRecord> Invitations,
    IReadOnlyList<AuditRecord> AuditEvents, OwnershipTransferRecord? PendingTransfer = null, int? AuditRetentionDays = null);
public interface IOrganizationAccess
{
    Task<OrganizationAccessView> ReadAsync(AccountContext context, CancellationToken cancellationToken);
    Task<CreatedInvitation> InviteAsync(AccountContext context, string email, string role, CancellationToken cancellationToken);
    Task RevokeInvitationAsync(AccountContext context, string id, CancellationToken cancellationToken);
    Task ChangeRoleAsync(AccountContext context, string userId, string role, CancellationToken cancellationToken);
    Task RemoveMemberAsync(AccountContext context, string userId, CancellationToken cancellationToken);
    Task TransferAsync(AccountContext context, string operation, string? userId, CancellationToken cancellationToken);
}

// Members, invitations, ownership transfer and audit events are ts-auth's. ts-auth
// rechecks the caller's role on every call; the local checks only shape the UI answer.
public sealed class TsOrganizationAccess(ITsAuthPlatform platform) : IOrganizationAccess
{
    private static string Organization(AccountContext context) => context.Organization?.Id ?? throw new AccountAccessException("Select an organization.");
    private static void Manage(AccountContext context)
    {
        if (!context.CanManage) throw new AccountAccessException("Organization management is required.");
    }
    public async Task<OrganizationAccessView> ReadAsync(AccountContext context, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        cancellationToken = deadline.Token;
        var id = Organization(context);
        var members = (await platform.ListMembersAsync(context.Session, id, cancellationToken)).Select(remote =>
        {
            var saved = context.Organization!.Members.GetValueOrDefault(remote.UserId);
            return new MemberRecord { UserId = remote.UserId, Role = remote.Role, Name = saved?.Name is { Length: > 0 } name ? name : remote.Email, Email = remote.Email, CreatedAt = remote.JoinedAt, JoinedOn = saved?.JoinedOn };
        }).ToArray();
        var invitations = new List<InvitationRecord>();
        var audit = new List<AuditRecord>();
        if (context.CanManage)
        {
            invitations.AddRange((await platform.ListInvitationsAsync(context.Session, id, cancellationToken)).Select(remote => Invitation(id, remote)));
            audit.AddRange((await platform.ListAuditEventsAsync(context.Session, id, cancellationToken)).Select(item =>
                new AuditRecord(item.Id, item.Actor, item.Action, item.Target ?? "", item.Detail ?? "", item.OccurredAt, Source: "ts-auth")));
        }
        var transfer = await platform.GetOwnershipTransferAsync(context.Session, id, cancellationToken);
        // ts-auth keeps audit events at least 90 days (its list_audit_events contract) and has no retention setting.
        return new(members, invitations, audit.Concat(context.Organization!.AuditEvents).OrderByDescending(item => item.OccurredAt).ToArray(),
            transfer is null ? null : new(transfer.FromUserId, transfer.ToUserId, transfer.CreatedAt, transfer.ExpiresAt), context.CanManage ? 90 : null);
    }
    public async Task<CreatedInvitation> InviteAsync(AccountContext context, string email, string role, CancellationToken cancellationToken)
    {
        Manage(context);
        if (!MailAddress.TryCreate(email, out var parsed) || !string.Equals(parsed.Address, email, StringComparison.OrdinalIgnoreCase)) throw new AccountAccessException("Enter a valid email address.", 422);
        var created = await platform.CreateInvitationAsync(context.Session, Organization(context), email, Role(role), cancellationToken);
        return new(Invitation(Organization(context), created.Invitation), created.Token);
    }
    public Task RevokeInvitationAsync(AccountContext context, string id, CancellationToken cancellationToken)
    { Manage(context); return platform.RevokeInvitationAsync(context.Session, Organization(context), id, cancellationToken); }
    public Task ChangeRoleAsync(AccountContext context, string userId, string role, CancellationToken cancellationToken)
    { Manage(context); ProtectOwner(context, userId); return platform.ChangeMemberRoleAsync(context.Session, Organization(context), userId, Role(role), cancellationToken); }
    public Task RemoveMemberAsync(AccountContext context, string userId, CancellationToken cancellationToken)
    { Manage(context); ProtectOwner(context, userId); return platform.RemoveMemberAsync(context.Session, Organization(context), userId, cancellationToken); }
    public async Task TransferAsync(AccountContext context, string operation, string? userId, CancellationToken cancellationToken)
    {
        var org = Organization(context);
        switch (operation)
        {
            case "start":
                if (context.Role != "owner") throw new AccountAccessException("Organization ownership is required.");
                if (string.IsNullOrWhiteSpace(userId) || userId == context.UserId) throw new AccountAccessException("Choose another active member as the new owner.", 422);
                await platform.StartOwnershipTransferAsync(context.Session, org, userId, cancellationToken); break;
            case "confirm":
                try { await platform.ConfirmOwnershipTransferAsync(context.Session, org, cancellationToken); }
                catch (AccountAccessException error) when (error.StatusCode == StatusCodes.Status403Forbidden)
                { throw new AccountAccessException("Only the nominated member can confirm ownership transfer.", 403); }
                break;
            case "cancel": await platform.CancelOwnershipTransferAsync(context.Session, org, cancellationToken); break;
            default: throw new AccountAccessException("Unknown transfer operation.", 422);
        }
    }
    public static InvitationRecord Invitation(string organizationId, PlatformInvitation remote) => new()
    {
        Id = remote.Id,
        OrganizationId = organizationId,
        Email = remote.Email,
        Role = remote.Role,
        Status = remote.Status,
        CreatedAt = remote.CreatedAt,
        ExpiresAt = remote.ExpiresAt
    };
    private static void ProtectOwner(AccountContext context, string userId)
    {
        if (userId == context.UserId || userId == context.Organization?.OwnerUserId) throw new AccountAccessException("Choose another member than yourself or the owner.", 422);
    }
    private static string Role(string role) => role is "viewer" or "member" or "admin" ? role : throw new AccountAccessException("Choose an available role.", 422);
}

using System.Net.Mail;
using TerraScale.Dashboard.Features.Accounts;
using TerraScale.Dashboard.Platform.Persistence;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.Dashboard.Features.Organizations;

public sealed record OrganizationAccessView(IReadOnlyList<MemberRecord> Members, IReadOnlyList<TerraBaseInvitation> Invitations,
    IReadOnlyList<AuditRecord> AuditEvents, TerraBaseOwnershipTransfer? PendingTransfer = null, int? AuditRetentionDays = null);
public interface IOrganizationAccess
{
    Task<OrganizationAccessView> ReadAsync(AccountContext context, CancellationToken cancellationToken);
    Task<TerraBaseCreatedInvitation> InviteAsync(AccountContext context, string email, string role, CancellationToken cancellationToken);
    Task RevokeInvitationAsync(AccountContext context, string id, CancellationToken cancellationToken);
    Task ChangeRoleAsync(AccountContext context, string userId, string role, CancellationToken cancellationToken);
    Task RemoveMemberAsync(AccountContext context, string userId, CancellationToken cancellationToken);
    Task TransferAsync(AccountContext context, string operation, string? userId, CancellationToken cancellationToken);
}

public sealed class TsOrganizationAccess(IHttpClientFactory http) : IOrganizationAccess
{
    private TerraBaseManagementClient Client(AccountContext context)
    {
        var client = http.CreateClient("platform");
        return new(client, new TerraBaseManagementClientOptions { EndpointResolver = new TerraBaseDiscovery(client), AccessTokenProvider = _ => ValueTask.FromResult(context.Session.AccessToken) });
    }
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
        var client = Client(context);
        var members = new List<MemberRecord>();
        var cursors = new HashSet<string>(StringComparer.Ordinal);
        string? after = null;
        do
        {
            var page = await client.ListMembersAsync(context.UserId, id, after, 100, cancellationToken);
            foreach (var remote in page.Items)
            {
                var saved = context.Organization!.Members.GetValueOrDefault(remote.UserId);
                members.Add(new() { UserId = remote.UserId, Role = remote.Role.ToString().ToLowerInvariant(), Name = saved?.Name ?? remote.UserId, Email = saved?.Email ?? "", CreatedAt = remote.JoinedAt, JoinedOn = saved?.JoinedOn });
            }
            after = page.Next;
            if (!string.IsNullOrEmpty(after) && !cursors.Add(after)) throw new IOException("Organization membership pagination did not advance.");
        } while (!string.IsNullOrEmpty(after));
        var invitations = new List<TerraBaseInvitation>();
        var audit = new List<AuditRecord>();
        int? retention = null;
        if (context.CanManage)
        {
            var policy = await client.GetAuditRetentionAsync(context.UserId, id, cancellationToken);
            if (policy.Days is < 1 or > 90) throw new IOException("Audit retention policy is invalid.");
            retention = policy.Days;
            after = null;
            cursors.Clear();
            do
            {
                var page = await client.ListInvitationsAsync(context.UserId, id, after, 100, cancellationToken);
                invitations.AddRange(page.Items); after = page.Next;
                if (!string.IsNullOrEmpty(after) && !cursors.Add(after)) throw new IOException("Invitation pagination did not advance.");
            } while (!string.IsNullOrEmpty(after));
            after = null;
            cursors.Clear();
            do
            {
                var page = await client.ListAuditEventsAsync(context.UserId, id, after, 100, cancellationToken);
                audit.AddRange(page.Items.Select(item => new AuditRecord(item.Id, item.Actor, item.Action, item.Target ?? "", item.Detail ?? "", item.OccurredAt, Source: "terrabase")));
                after = page.Next;
                if (!string.IsNullOrEmpty(after) && !cursors.Add(after)) throw new IOException("Audit pagination did not advance.");
            } while (!string.IsNullOrEmpty(after));
        }
        TerraBaseOwnershipTransfer? transfer;
        try { transfer = await client.GetOwnershipTransferAsync(context.UserId, id, cancellationToken); }
        catch (TerraBaseNotFoundException) { transfer = null; }
        return new(members, invitations, audit.Concat(context.Organization!.AuditEvents).OrderByDescending(item => item.OccurredAt).ToArray(), transfer, retention);
    }
    public Task<TerraBaseCreatedInvitation> InviteAsync(AccountContext context, string email, string role, CancellationToken cancellationToken)
    {
        Manage(context);
        if (!MailAddress.TryCreate(email, out var parsed) || !string.Equals(parsed.Address, email, StringComparison.OrdinalIgnoreCase)) throw new AccountAccessException("Enter a valid email address.", 422);
        return Client(context).CreateInvitationAsync(context.UserId, Organization(context), email, Role(role), cancellationToken);
    }
    public Task RevokeInvitationAsync(AccountContext context, string id, CancellationToken cancellationToken)
    { Manage(context); return Client(context).RevokeInvitationAsync(context.UserId, Organization(context), id, cancellationToken); }
    public async Task ChangeRoleAsync(AccountContext context, string userId, string role, CancellationToken cancellationToken)
    { Manage(context); ProtectOwner(context, userId); _ = await Client(context).ChangeMemberRoleAsync(context.UserId, Organization(context), userId, Role(role), cancellationToken); }
    public Task RemoveMemberAsync(AccountContext context, string userId, CancellationToken cancellationToken)
    { Manage(context); ProtectOwner(context, userId); return Client(context).RemoveMemberAsync(context.UserId, Organization(context), userId, cancellationToken); }
    public async Task TransferAsync(AccountContext context, string operation, string? userId, CancellationToken cancellationToken)
    {
        var client = Client(context);
        var org = Organization(context);
        switch (operation)
        {
            case "start":
                if (context.Role != "owner") throw new AccountAccessException("Organization ownership is required.");
                if (string.IsNullOrWhiteSpace(userId) || userId == context.UserId) throw new AccountAccessException("Choose another active member as the new owner.", 422);
                _ = await client.StartOwnershipTransferAsync(context.UserId, org, userId, cancellationToken); break;
            case "confirm":
                try { _ = await client.ConfirmOwnershipTransferAsync(context.UserId, org, cancellationToken); }
                catch (TerraBaseForbiddenException) { throw new AccountAccessException("Only the nominated member can confirm ownership transfer.", 403); }
                break;
            case "cancel": await client.CancelOwnershipTransferAsync(context.UserId, org, cancellationToken); break;
            default: throw new AccountAccessException("Unknown transfer operation.", 422);
        }
    }
    private static void ProtectOwner(AccountContext context, string userId)
    {
        if (userId == context.UserId || userId == context.Organization?.OwnerUserId) throw new AccountAccessException("Choose another member than yourself or the owner.", 422);
    }
    private static TerraBaseRole Role(string role) => role switch
    {
        "member" => TerraBaseRole.Member,
        "admin" => TerraBaseRole.Admin,
        _ => throw new AccountAccessException("Choose an available role.", 422)
    };
}

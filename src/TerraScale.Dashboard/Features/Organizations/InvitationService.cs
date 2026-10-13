using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Net.Mail;
using TerraScale.Dashboard.Features.Accounts;
using TerraScale.Dashboard.Platform.Persistence;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.Dashboard.Features.Organizations;

public interface IInvitationGateway
{
    Task<TerraBaseMember> AcceptAsync(SessionRecord session, string token, CancellationToken cancellationToken);
}

public sealed class TsInvitationGateway(IHttpClientFactory http) : IInvitationGateway
{
    public Task<TerraBaseMember> AcceptAsync(SessionRecord session, string token, CancellationToken cancellationToken)
    {
        var client = http.CreateClient("platform");
        return new TerraBaseManagementClient(client, new()
        {
            EndpointResolver = new TerraBaseDiscovery(),
            AccessTokenProvider = _ => ValueTask.FromResult(session.AccessToken)
        }).AcceptInvitationAsync(session.UserId, token, cancellationToken);
    }
}

public sealed record PendingInvitation(OrganizationRecord Organization, InvitationRecord Invitation);
public sealed record InvitationTokenIndex(string OrganizationId, string InvitationId);

// Token indexes contain only digests; the provider secret is shown once after
// creation and is never saved in a dashboard state document.
public sealed class InvitationService(IDashboardState state, AccountService accounts, IOrganizationAccess access,
    IInvitationGateway gateway, IWorkspaceGateway workspaces, TimeProvider time, ILogger<InvitationService> logger)
{
    public const string Collection = "dashboard_invitation_tokens";
    public static string Hash(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    public async Task<TerraBaseCreatedInvitation> CreateAsync(AccountContext context, string email, string role, CancellationToken cancellationToken)
    {
        context = await FreshAsync(context, cancellationToken);
        email = email.Trim().ToLowerInvariant();
        role = string.IsNullOrWhiteSpace(role) ? "member" : role.Trim().ToLowerInvariant();
        OrganizationOperations.AvailableRole(role);
        if (!MailAddress.TryCreate(email, out var parsed) || !email.Equals(parsed.Address, StringComparison.OrdinalIgnoreCase))
            throw new AccountAccessException("Enter a valid email address.", 422);
        var snapshot = await access.ReadAsync(context, cancellationToken);
        Capacity(context.Organization!, snapshot, email);
        var created = await access.InviteAsync(context, email, role, cancellationToken);
        var remote = created.Invitation;
        if (remote.Id.Length == 0 || created.Token.Length == 0 || !remote.Email.Equals(email.Trim(), StringComparison.OrdinalIgnoreCase))
            throw new IOException("Invitation provisioning returned an invalid identity.");
        var record = new InvitationRecord
        {
            Id = remote.Id,
            OrganizationId = context.Organization!.Id,
            Email = remote.Email,
            Role = remote.Role.ToString().ToLowerInvariant(),
            TokenHash = Hash(created.Token),
            CreatedAt = remote.CreatedAt,
            ExpiresAt = remote.ExpiresAt
        };
        try
        {
            await accounts.UpdateOrganizationAsync(context, org =>
            {
                OrganizationOperations.Owner(org, context.UserId);
                Capacity(org, snapshot, email);
                if (org.Invitations.ContainsKey(record.Id)) throw new AccountAccessException("Invitation identity already exists.", 409);
                org.Invitations[record.Id] = record;
                org.AuditEvents.Add(new(AccountService.NewId(), context.Account.Email, "invitation.created", email, "invitation", time.GetUtcNow()));
            }, cancellationToken);
        }
        catch
        {
            // A quota or membership race must not leave a usable provider link
            // that was never admitted by the canonical CAS transaction.
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await access.RevokeInvitationAsync(context, record.Id, deadline.Token); }
            catch (Exception error) when (error is IOException or HttpRequestException || error is OperationCanceledException)
            { logger.LogWarning(error, "Revoke uncommitted invitation {InvitationId}", record.Id); }
            throw;
        }
        await state.PutAsync(Collection, record.TokenHash, JsonSerializer.SerializeToElement(new InvitationTokenIndex(record.OrganizationId, record.Id)), TerraBasePrecondition.Absent, cancellationToken);
        return created;
    }

    public async Task<TerraBaseCreatedInvitation> ResendAsync(AccountContext context, string id, CancellationToken cancellationToken)
    {
        context = await FreshAsync(context, cancellationToken);
        var snapshot = await access.ReadAsync(context, cancellationToken);
        var remote = snapshot.Invitations.FirstOrDefault(invitation => invitation.Id == id);
        var saved = context.Organization!.Invitations.GetValueOrDefault(id);
        if (remote is null && saved is not { Status: "pending" } || saved is not null && saved.Status != "pending")
            throw new AccountAccessException("Invitation is unavailable.", 404);
        // Some producers omit expired invitations. The canonical pending
        // record retains its verified org/email/role, so an owner can replace
        // it after the provider has already removed the old resource.
        if (remote is null && saved!.ExpiresAt > time.GetUtcNow()) throw new AccountAccessException("Invitation is unavailable.", 404);
        try { await access.RevokeInvitationAsync(context, id, cancellationToken); }
        catch (TerraBaseNotFoundException) when (saved is { Status: "pending" } && saved.ExpiresAt <= time.GetUtcNow()) { }
        await accounts.UpdateOrganizationAsync(context, org =>
        {
            OrganizationOperations.Owner(org, context.UserId);
            if (org.Invitations.TryGetValue(id, out var current))
            {
                if (current.Status != "pending") throw new AccountAccessException("Invitation changed. Reload invitations.", 409);
                current.Status = "revoked";
            }
        }, cancellationToken);
        return await CreateAsync(context, remote?.Email ?? saved!.Email, remote?.Role.ToString().ToLowerInvariant() ?? saved!.Role, cancellationToken);
    }

    public async Task RevokeAsync(AccountContext context, string id, CancellationToken cancellationToken)
    {
        context = await FreshAsync(context, cancellationToken);
        var saved = context.Organization!.Invitations.GetValueOrDefault(id);
        if (saved is not { Status: "pending" } || saved.OrganizationId != context.Organization.Id)
            throw new AccountAccessException("Invitation is unavailable.", 404);
        try { await access.RevokeInvitationAsync(context, id, cancellationToken); }
        catch (TerraBaseNotFoundException) when (saved.ExpiresAt <= time.GetUtcNow()) { }
        catch (TerraBaseNotFoundException) { throw new AccountAccessException("Invitation is unavailable.", 404); }
        await accounts.UpdateOrganizationAsync(context, org =>
        {
            OrganizationOperations.Owner(org, context.UserId);
            if (!org.Invitations.TryGetValue(id, out var invitation) || invitation.OrganizationId != org.Id || invitation.Status != "pending")
                throw new AccountAccessException("Invitation is unavailable.", 404);
            invitation.Status = "revoked";
        }, cancellationToken);
    }

    public async Task<PendingInvitation?> FindAsync(string token, CancellationToken cancellationToken)
    {
        var found = await ReadAsync(token, cancellationToken);
        return found is not null && found.Invitation.Status == "pending" && found.Invitation.ExpiresAt > time.GetUtcNow() ? found : null;
    }

    private async Task<PendingInvitation?> ReadAsync(string token, CancellationToken cancellationToken)
    {
        if (token.Length is 0 or > 4096 || token.Any(char.IsControl)) return null;
        var hash = Hash(token);
        var indexDocument = await state.GetAsync(Collection, hash, cancellationToken);
        if (indexDocument is null) return null;
        if (indexDocument.Id != hash) throw new IOException("Invitation index identity changed.");
        var index = AccountService.Read<InvitationTokenIndex>(indexDocument);
        var document = await state.GetAsync(AccountService.OrganizationsCollection, index.OrganizationId, cancellationToken);
        if (document is null) return null;
        if (document.Id != index.OrganizationId) throw new IOException("Invitation organization identity changed.");
        var org = AccountService.Read<OrganizationRecord>(document);
        return org.Id == index.OrganizationId && org.Status == "active" && !org.Archived && !org.DeletionRequested &&
            org.Invitations.TryGetValue(index.InvitationId, out var invite) && invite.OrganizationId == org.Id &&
            invite.TokenHash == hash ? new(org, invite) : null;
    }

    public async Task<OrganizationRecord> RespondAsync(SessionRecord session, string token, string action, CancellationToken cancellationToken)
    {
        action = action.Trim().ToLowerInvariant();
        if (action is not ("accept" or "decline")) throw new AccountAccessException("Choose whether to accept or decline the invitation.", 400);
        var pending = await ReadAsync(token, cancellationToken) ?? throw new AccountAccessException("Invitation is unavailable.", 404);
        if (!pending.Invitation.Email.Equals(session.Email, StringComparison.OrdinalIgnoreCase))
            throw new AccountAccessException("Sign in with the invited email to answer this invitation.");
        if (action == "accept" && pending.Invitation.Status == "accepted" && pending.Invitation.RespondedByUserId == session.UserId)
        {
            if (await workspaces.RoleAsync(session, pending.Organization.Id, cancellationToken) is not { Length: > 0 }) throw new AccountAccessException("Organization membership is required.");
            await LinkAsync(session, pending.Organization, cancellationToken);
            return pending.Organization;
        }
        if (pending.Invitation.Status != "pending" || pending.Invitation.ExpiresAt <= time.GetUtcNow()) throw new AccountAccessException("Invitation is unavailable.", 404);
        TerraBaseMember? member = null;
        if (action == "accept")
        {
            try { member = await gateway.AcceptAsync(session, token, cancellationToken); }
            catch (TerraBaseNotFoundException)
            {
                // A prior accepted provider token can be consumed before the
                // dashboard CAS commits. Recover only a verified live member.
                if (await workspaces.RoleAsync(session, pending.Organization.Id, cancellationToken) is not { Length: > 0 }) throw;
                var account = await accounts.EnsureAsync(session, cancellationToken);
                var joined = (await access.ReadAsync(new(session, account, [pending.Organization], pending.Organization, null), cancellationToken)).Members.FirstOrDefault(candidate => candidate.UserId == session.UserId);
                if (joined is null) throw;
                member = new(joined.UserId, joined.Role switch
                {
                    "owner" => TerraBaseRole.Owner,
                    "admin" => TerraBaseRole.Admin,
                    "member" => TerraBaseRole.Member,
                    _ => throw new AccountAccessException("Invitation membership role is unavailable.")
                }, joined.CreatedAt);
            }
            if (member.UserId != session.UserId || await workspaces.RoleAsync(session, pending.Organization.Id, cancellationToken) is not { Length: > 0 })
                throw new AccountAccessException("Invitation membership could not be verified.");
        }
        // Declining is dashboard-owned persistent state, matching the Go flow.
        // The current producer has no invitee-authorized decline/revoke route.
        for (var attempt = 0; ; attempt++)
        {
            var document = await state.GetAsync(AccountService.OrganizationsCollection, pending.Organization.Id, cancellationToken)
                ?? throw new AccountAccessException("Organization is unavailable.", 404);
            var org = AccountService.Read<OrganizationRecord>(document);
            if (org.Id != pending.Organization.Id || org.Status != "active" || org.DeletionRequested || org.Archived ||
                !org.Invitations.TryGetValue(pending.Invitation.Id, out var invitation) || invitation.OrganizationId != org.Id ||
                invitation.TokenHash != Hash(token) || invitation.Status != "pending" || invitation.ExpiresAt <= time.GetUtcNow() ||
                !invitation.Email.Equals(session.Email, StringComparison.OrdinalIgnoreCase))
                throw new AccountAccessException("Invitation is unavailable.", 404);
            invitation.Status = action == "accept" ? "accepted" : "declined";
            invitation.RespondedByUserId = session.UserId;
            if (member is not null) org.Members[session.UserId] = new() { UserId = session.UserId, Name = session.Name, Email = session.Email, Role = member.Role.ToString().ToLowerInvariant(), CreatedAt = member.JoinedAt };
            org.AuditEvents.Add(new(AccountService.NewId(), session.Email, "invitation." + invitation.Status, invitation.Email, "invitation", time.GetUtcNow()));
            try
            {
                await state.PutAsync(AccountService.OrganizationsCollection, org.Id, JsonSerializer.SerializeToElement(org), TerraBasePrecondition.Matches(document.Version), cancellationToken);
                pending = new(org, invitation);
                break;
            }
            catch (TerraBasePreconditionFailedException) when (attempt < 3) { }
        }
        if (action == "accept") await LinkAsync(session, pending.Organization, cancellationToken);
        return pending.Organization;
    }

    private async Task LinkAsync(SessionRecord session, OrganizationRecord organization, CancellationToken cancellationToken)
    {
        await accounts.EnsureAsync(session, cancellationToken);
        await accounts.UpdateAccountAsync(session, account =>
        {
            if (!account.OrganizationIds.Contains(organization.Id)) account.OrganizationIds.Add(organization.Id);
            account.ActiveOrganizationId = organization.Id;
            account.ActiveProjectId = organization.Projects.Values.FirstOrDefault(project => project.IsDefault)?.Id ?? organization.Projects.Keys.Order().FirstOrDefault() ?? "";
        }, cancellationToken);
    }

    private async Task<AccountContext> FreshAsync(AccountContext context, CancellationToken cancellationToken)
    {
        var fresh = await AccountScope.RefreshAsync(accounts, context, cancellationToken);
        OrganizationOperations.Owner(fresh.Organization!, fresh.UserId);
        return fresh;
    }

    private void Capacity(OrganizationRecord org, OrganizationAccessView snapshot, string email)
    {
        if (org.DeletionRequested) throw new AccountAccessException("Organization deletion has been requested.", 409);
        var members = snapshot.Members.Concat(org.Members.Values).Where(member => member.Status == "active")
            .GroupBy(member => member.UserId).Select(group => group.First()).ToArray();
        var pending = snapshot.Invitations.Where(invite => invite.ExpiresAt > time.GetUtcNow() &&
                (!org.Invitations.TryGetValue(invite.Id, out var stored) || stored.Status == "pending"))
            .Select(invite => (invite.Id, invite.Email)).Concat(org.Invitations.Values
                .Where(invite => invite.Status == "pending" && invite.ExpiresAt > time.GetUtcNow()).Select(invite => (invite.Id, invite.Email)))
            .GroupBy(invite => invite.Id).Select(group => group.First()).ToArray();
        if (members.Any(member => member.Email.Equals(email, StringComparison.OrdinalIgnoreCase)))
            throw new AccountAccessException("This email is already an active member.", 409);
        if (pending.Any(invite => invite.Email.Equals(email, StringComparison.OrdinalIgnoreCase)))
            throw new AccountAccessException("This email already has a pending invitation.", 409);
        var plan = PlanCatalog.For(org)
            ?? throw new AccountAccessException("The organization's member allowance is unavailable.", 409);
        var limit = PlanCatalog.ResourceLimits(org).Members;
        if (limit > 0 && members.Length + pending.Length >= limit)
            throw new AccountAccessException($"The {plan.Name} plan allows {limit} members including pending invitations.", 409);
    }
}

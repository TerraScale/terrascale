using System.Net.Mail;
using TerraScale.Dashboard.Features.Accounts;
using TerraScale.Dashboard.Features.Auth;
using TerraScale.Dashboard.Features.Billing;

namespace TerraScale.Dashboard.Features.Organizations;

public sealed record PendingInvitation(OrganizationRecord Organization, InvitationRecord Invitation);

// ts-auth stores invitations and their tokens, checks the invitee's verified
// email and grants the membership. The dashboard keeps no token or index; it
// only applies its plan's member allowance before asking ts-auth to invite.
public sealed class InvitationService(AccountService accounts, IOrganizationAccess access, ITsAuthPlatform platform, TimeProvider time)
{
    public async Task<CreatedInvitation> CreateAsync(AccountContext context, string email, string role, CancellationToken cancellationToken)
    {
        context = await FreshAsync(context, cancellationToken);
        email = email.Trim().ToLowerInvariant();
        role = string.IsNullOrWhiteSpace(role) ? "member" : role.Trim().ToLowerInvariant();
        OrganizationOperations.AvailableRole(role);
        if (!MailAddress.TryCreate(email, out var parsed) || !email.Equals(parsed.Address, StringComparison.OrdinalIgnoreCase))
            throw new AccountAccessException("Enter a valid email address.", 422);
        var snapshot = await access.ReadAsync(context, cancellationToken);
        // Reserve the slot first: the compare-and-set makes concurrent invitations on any replica see each other.
        await accounts.UpdateOrganizationAsync(context, org =>
        {
            OrganizationOperations.Owner(org, context.UserId);
            foreach (var expired in org.InvitationReservations.Where(item => item.Value <= time.GetUtcNow()).Select(item => item.Key).ToArray())
                org.InvitationReservations.Remove(expired);
            Capacity(org, snapshot, email);
            org.InvitationReservations[email] = time.GetUtcNow().AddMinutes(1);
        }, cancellationToken);
        try { return await access.InviteAsync(context, email, role, cancellationToken); }
        catch
        {
            using var release = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await accounts.UpdateOrganizationAsync(context, org => org.InvitationReservations.Remove(email), release.Token); }
            catch (Exception error) when (error is HttpRequestException or IOException or AccountAccessException or OperationCanceledException) { }
            throw;
        }
    }

    public async Task<CreatedInvitation> ResendAsync(AccountContext context, string id, CancellationToken cancellationToken)
    {
        context = await FreshAsync(context, cancellationToken);
        var snapshot = await access.ReadAsync(context, cancellationToken);
        var remote = snapshot.Invitations.FirstOrDefault(invitation => invitation.Id == id && invitation.Status is "pending" or "expired")
            ?? throw new AccountAccessException("Invitation is unavailable.", 404);
        // An expired invitation is no longer revocable at ts-auth; its replacement is still allowed.
        if (remote.Status == "pending") await access.RevokeInvitationAsync(context, id, cancellationToken);
        return await CreateAsync(context, remote.Email, remote.Role, cancellationToken);
    }

    public async Task RevokeAsync(AccountContext context, string id, CancellationToken cancellationToken)
    {
        context = await FreshAsync(context, cancellationToken);
        var email = (await access.ReadAsync(context, cancellationToken)).Invitations.FirstOrDefault(invitation => invitation.Id == id)?.Email.ToLowerInvariant();
        await access.RevokeInvitationAsync(context, id, cancellationToken);
        // A revoked invitation frees its slot at once instead of when its reservation expires.
        if (email is not null) await accounts.UpdateOrganizationAsync(context, org => org.InvitationReservations.Remove(email), cancellationToken);
    }

    // ts-auth answers a lookup only to a signed-in user holding the token.
    public async Task<PendingInvitation?> FindAsync(SessionRecord? session, string token, CancellationToken cancellationToken)
    {
        if (session is null || token.Length is 0 or > 4096 || token.Any(char.IsControl)) return null;
        var found = await platform.LookupInvitationAsync(session, token, cancellationToken);
        if (found is null || found.ExpiresAt <= time.GetUtcNow()) return null;
        return new(new OrganizationRecord { Id = found.Organization.Id, Name = found.Organization.Name },
            new InvitationRecord { OrganizationId = found.Organization.Id, Email = found.Email, Role = found.Role, ExpiresAt = found.ExpiresAt });
    }

    public async Task<OrganizationRecord> RespondAsync(SessionRecord session, string token, string action, CancellationToken cancellationToken)
    {
        action = action.Trim().ToLowerInvariant();
        if (action is not ("accept" or "decline")) throw new AccountAccessException("Choose whether to accept or decline the invitation.", 400);
        var pending = await FindAsync(session, token, cancellationToken) ?? throw new AccountAccessException("Invitation is unavailable.", 404);
        if (!pending.Invitation.Email.Equals(session.Email, StringComparison.OrdinalIgnoreCase))
            throw new AccountAccessException("Sign in with the invited email to answer this invitation.");
        if (action == "decline")
        {
            await platform.DeclineInvitationAsync(session, token, cancellationToken);
            return pending.Organization;
        }
        var accepted = await platform.AcceptInvitationAsync(session, token, cancellationToken);
        if (accepted.OrganizationId != pending.Organization.Id) throw new IOException("ts-auth accepted another organization's invitation.");
        // The membership is ts-auth's; select the organization the caller joined.
        await accounts.SwitchOrganizationAsync(session, accepted.OrganizationId, cancellationToken);
        return pending.Organization;
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
        var members = snapshot.Members.Where(member => member.Status == "active").ToArray();
        var listed = snapshot.Invitations.Where(invite => invite.Status == "pending" && invite.ExpiresAt > time.GetUtcNow()).Select(invite => invite.Email.ToLowerInvariant()).ToArray();
        if (members.Any(member => member.Email.Equals(email, StringComparison.OrdinalIgnoreCase)))
            throw new AccountAccessException("This email is already an active member.", 409);
        if (listed.Contains(email, StringComparer.OrdinalIgnoreCase))
            throw new AccountAccessException("This email already has a pending invitation.", 409);
        // Recent reservations count until ts-auth lists them, one slot per email (ts-auth refuses a second
        // pending invitation for the same email itself).
        var pending = listed.Concat(org.InvitationReservations.Where(item => item.Value > time.GetUtcNow() && item.Key != email).Select(item => item.Key)).Distinct().ToArray();
        var plan = PlanCatalog.For(org)
            ?? throw new AccountAccessException("The organization's member allowance is unavailable.", 409);
        var limit = PlanCatalog.ResourceLimits(org).Members;
        if (limit > 0 && members.Length + pending.Length >= limit)
            throw new AccountAccessException($"The {plan.Name} plan allows {limit} members including pending invitations.", 409);
    }
}

using System.Net.Mail;
using TerraScale.Dashboard.Features.Accounts;
using TerraScale.Dashboard.Features.Auth;
using TerraScale.Dashboard.Features.Billing;
using TerraScale.DotNet;

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
        // Take the slot in the same compare-and-set that reads ts-auth's current invitations and members,
        // so a decision is never based on state older than the reservations it can see.
        var reservation = AccountService.NewId();
        await accounts.UpdateOrganizationAsync(context, async org =>
        {
            OrganizationOperations.Owner(org, context.UserId);
            // Invitations before members: an acceptance in between shows the invitee in both, never in neither.
            var invitations = await platform.ListInvitationsAsync(context.Session, org.Id, cancellationToken);
            var members = await platform.ListMembersAsync(context.Session, org.Id, cancellationToken);
            Reconcile(org, invitations);
            Capacity(org, members, invitations, email);
            org.InvitationReservations[reservation] = new(email, time.GetUtcNow());
        }, cancellationToken);
        CreatedInvitation created;
        try { created = await access.InviteAsync(context, email, role, cancellationToken); }
        catch (AccountAccessException)
        {
            // ts-auth refused, so nothing was created: free only this request's slot.
            await ReleaseAsync(context, reservation);
            throw;
        }
        // Any other failure keeps the reservation: ts-auth may have created the invitation, and the
        // next decision reconciles it against ts-auth's list.
        try
        {
            await accounts.UpdateOrganizationAsync(context, org =>
            {
                if (org.InvitationReservations.TryGetValue(reservation, out var current))
                    org.InvitationReservations[reservation] = current with { InvitationId = created.Invitation.Id };
            }, cancellationToken);
        }
        catch (Exception error) when (error is HttpRequestException or IOException or OperationCanceledException) { }
        return created;
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

    // A revoked invitation frees its slot through ts-auth's list; its reservation, if any, reconciles there.
    public async Task RevokeAsync(AccountContext context, string id, CancellationToken cancellationToken)
    {
        context = await FreshAsync(context, cancellationToken);
        await access.RevokeInvitationAsync(context, id, cancellationToken);
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

    private async Task ReleaseAsync(AccountContext context, string reservation)
    {
        using var release = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try { await accounts.UpdateOrganizationAsync(context, org => org.InvitationReservations.Remove(reservation), release.Token); }
        catch (Exception error) when (error is HttpRequestException or IOException or AccountAccessException or OperationCanceledException) { }
    }

    // A reservation ends once ts-auth lists its invitation (by id, or by email for one whose response was lost),
    // or after AbandonAfter with no such invitation: a request never outlives its 30-second deadline.
    private void Reconcile(OrganizationRecord org, IReadOnlyList<TerraScaleAuthPlatformInvitation> invitations)
    {
        var now = time.GetUtcNow();
        foreach (var (id, reservation) in org.InvitationReservations.ToArray())
        {
            var listed = reservation.InvitationId.Length > 0
                ? invitations.Any(invitation => invitation.Id == reservation.InvitationId)
                : invitations.Any(invitation => invitation.Email.Equals(reservation.Email, StringComparison.OrdinalIgnoreCase) &&
                    invitation.Status is "pending" or "accepted" && invitation.CreatedAt >= reservation.ReservedAt - ClockSkew);
            if (listed || now - reservation.ReservedAt > AbandonAfter) org.InvitationReservations.Remove(id);
        }
    }

    // Occupied slots are distinct emails across members, ts-auth's pending invitations and reservations it
    // does not list yet, so an invitee who just accepted counts once.
    private void Capacity(OrganizationRecord org, IReadOnlyList<TerraScaleAuthPlatformMember> members, IReadOnlyList<TerraScaleAuthPlatformInvitation> invitations, string email)
    {
        if (org.DeletionRequested) throw new AccountAccessException("Organization deletion has been requested.", 409);
        var now = time.GetUtcNow();
        var memberEmails = members.Select(member => member.Email.ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);
        var pending = invitations.Where(invitation => invitation.Status == "pending" && invitation.ExpiresAt > now).Select(invitation => invitation.Email.ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);
        var reserved = org.InvitationReservations.Values.Select(reservation => reservation.Email).ToHashSet(StringComparer.Ordinal);
        if (memberEmails.Contains(email)) throw new AccountAccessException("This email is already an active member.", 409);
        if (pending.Contains(email)) throw new AccountAccessException("This email already has a pending invitation.", 409);
        if (reserved.Contains(email)) throw new AccountAccessException("An invitation for this email is already being created.", 409);
        var plan = PlanCatalog.For(org)
            ?? throw new AccountAccessException("The organization's member allowance is unavailable.", 409);
        var limit = PlanCatalog.ResourceLimits(org).Members;
        if (limit > 0 && memberEmails.Union(pending).Union(reserved).Count() >= limit)
            throw new AccountAccessException($"The {plan.Name} plan allows {limit} members including pending invitations.", 409);
    }

    private static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan AbandonAfter = TimeSpan.FromMinutes(15);
}

using System.Net.Mail;
using TerraScale.Dashboard.Features.Auth;
using TerraScale.Dashboard.Platform.TsAuth;

namespace TerraScale.Dashboard.Features.Organizations
{
    public sealed record PendingInvitation(OrganizationRecord Organization, InvitationRecord Invitation, TsAuthPlatformNamed? Project = null);

    // ts-auth stores invitations and their tokens, checks the invitee's verified
    // email and grants the membership. The dashboard keeps no token or index; it
    // only applies its plan's member allowance before asking ts-auth to invite.
    public sealed class InvitationService(AccountService accounts, IOrganizationAccess access, ITsAuthPlatform platform, TimeProvider time)
    {
        public Task<CreatedInvitation> CreateAsync(AccountContext context, string email, string role, CancellationToken cancellationToken)
        {
            return CreateAsync(context, email, role, null, cancellationToken);
        }

        private async Task<CreatedInvitation> CreateAsync(AccountContext context, string email, string role, string? projectId, CancellationToken cancellationToken)
        {
            context = await FreshAsync(context, cancellationToken);
            email = email.Trim().ToLowerInvariant();
            role = string.IsNullOrWhiteSpace(role) ? "member" : role.Trim().ToLowerInvariant();
            OrganizationOperations.AvailableRole(role);
            if (!MailAddress.TryCreate(email, out var parsed) || !email.Equals(parsed.Address, StringComparison.OrdinalIgnoreCase))
            {
                throw new AccountAccessException("Enter a valid email address.", 422);
            }
            // Take the slot in the same compare-and-set that reads ts-auth's current invitations and members,
            // so a decision is never based on state older than the reservations it can see.
            var reservation = AccountService.NewId();
            await accounts.UpdateOrganizationAsync(context, async org =>
            {
                OrganizationOperations.Manage(org, context.UserId);
                // Invitations before members: an acceptance in between shows the invitee in both, never in neither.
                var invitations = await platform.ListInvitationsAsync(context.Session, org.Id, cancellationToken);
                var members = await platform.ListMembersAsync(context.Session, org.Id, cancellationToken);
                Reconcile(org, invitations);
                Capacity(org, members, invitations, email);
                var taken = new InvitationReservation(email, time.GetUtcNow()) { ProjectId = projectId };
                org.InvitationReservations[reservation] = taken with { Known = [.. invitations.Where(invitation => Same(invitation, taken)).Select(invitation => invitation.Id)] };
            }, cancellationToken);
            CreatedInvitation created;
            try
            {
                // Recheck after reservation persistence: direct ts-auth membership writes do not change its version.
                // shortcut: direct identity writes after this read can still race; enforce global allowance in ts-auth.
                await accounts.UpdateOrganizationAsync(context, async org =>
                {
                    OrganizationOperations.Manage(org, context.UserId);
                    var invitations = await platform.ListInvitationsAsync(context.Session, org.Id, cancellationToken);
                    var members = await platform.ListMembersAsync(context.Session, org.Id, cancellationToken);
                    Reconcile(org, invitations);
                    Capacity(org, members, invitations, email);
                }, cancellationToken);
                created = await access.InviteAsync(context, email, role, projectId, cancellationToken);
            }
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
                    // Another reservation for this email already ended on this invitation, so this one stands for that request.
                    if (org.InvitationReservations.TryGetValue(reservation, out var current) && !current.Known.Contains(created.Invitation.Id))
                    {
                        org.InvitationReservations[reservation] = current with { InvitationId = created.Invitation.Id };
                    }
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
            // The replacement copies ts-auth's scope and role exactly: a project invitation is resent to that project, never
            // widened to the organization. A role or project id the dashboard cannot confirm refuses the resend before
            // anything is revoked; nothing is rebuilt from defaults.
            OrganizationOperations.AvailableRole(remote.Role);
            if (remote.ProjectId is { } projectId && (!TerraScaleIds.IsValid(projectId) ||
                !(await platform.ListProjectsAsync(context.Session, context.Organization!.Id, cancellationToken)).Any(project => project.Id == projectId)))
            {
                throw new AccountAccessException("This invitation's project is unavailable, so it cannot be resent.", 409);
            }
            // ts-auth lists an invitation past its expiry as expired but still stores it pending, so it could still be
            // accepted: store a revoke before creating the replacement.
            await access.RevokeInvitationAsync(context, id, cancellationToken);
            return await CreateAsync(context, remote.Email, remote.Role, remote.ProjectId, cancellationToken);
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
            if (session is null || token.Length is 0 or > 4096 || token.Any(char.IsControl))
            {
                return null;
            }

            var found = await platform.LookupInvitationAsync(session, token, cancellationToken);
            if (found is null || found.ExpiresAt <= time.GetUtcNow())
            {
                return null;
            }

            if (!ValidNamed(found.Organization) || found.Project is { } project && (!ValidNamed(project) || project.Id == found.Organization.Id) ||
                found.Role is not ("viewer" or "member" or "admin" or "owner") || found.Project is not null && found.Role == "owner")
            {
                throw new IOException("ts-auth invitation scope is invalid.");
            }

            return new(new OrganizationRecord { Id = found.Organization.Id, Name = found.Organization.Name },
                new InvitationRecord { OrganizationId = found.Organization.Id, ProjectId = found.Project?.Id, Email = found.Email, Role = found.Role, ExpiresAt = found.ExpiresAt }, found.Project);
        }

        private static bool ValidNamed(TsAuthPlatformNamed? named)
        {
            return named is not null && TerraScaleIds.IsValid(named.Id) &&
            named.Name is { Length: >= 1 and <= 128 } name && name == name.Trim() && name.Any(char.IsLetterOrDigit) && !name.Any(char.IsControl);
        }

        public async Task<PendingInvitation> RespondAsync(SessionRecord session, string token, string action, CancellationToken cancellationToken)
        {
            action = action.Trim().ToLowerInvariant();
            if (action is not ("accept" or "decline"))
            {
                throw new AccountAccessException("Choose whether to accept or decline the invitation.", 400);
            }

            var pending = await FindAsync(session, token, cancellationToken) ?? throw new AccountAccessException("Invitation is unavailable.", 404);
            if (!pending.Invitation.Email.Equals(session.Email, StringComparison.OrdinalIgnoreCase))
            {
                throw new AccountAccessException("Sign in with the invited email to answer this invitation.");
            }

            if (action == "decline")
            {
                await platform.DeclineInvitationAsync(session, token, cancellationToken);
                return pending;
            }
            var accepted = await platform.AcceptInvitationAsync(session, token, cancellationToken);
            if (accepted.OrganizationId != pending.Organization.Id || accepted.ProjectId != pending.Invitation.ProjectId || accepted.Role != pending.Invitation.Role)
            {
                throw new IOException("ts-auth accepted a different invitation scope.");
            }

            if (accepted.ProjectId is { } projectId)
            {
                // Confirm the pair through live authorization before changing the saved selection.
                await accounts.GetContextAsync(session, cancellationToken, accepted.OrganizationId, projectId);
                await accounts.SwitchProjectAsync(session, projectId, cancellationToken);
            }
            else
            {
                await accounts.SwitchOrganizationAsync(session, accepted.OrganizationId, cancellationToken);
            }

            return pending;
        }

        private async Task<AccountContext> FreshAsync(AccountContext context, CancellationToken cancellationToken)
        {
            var fresh = await AccountScope.RefreshAsync(accounts, context, cancellationToken);
            OrganizationOperations.Manage(fresh.Organization!, fresh.UserId);
            return fresh;
        }

        private async Task ReleaseAsync(AccountContext context, string reservation)
        {
            using var release = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await accounts.UpdateOrganizationAsync(context, org => org.InvitationReservations.Remove(reservation), release.Token); }
            catch (Exception error) when (error is HttpRequestException or IOException or AccountAccessException or OperationCanceledException) { }
        }

        // ts-auth has no idempotency key or cancellation for invitation creation, so a request with an unknown outcome
        // may commit at any later time, and neither elapsed time, clocks nor an absent entry end its reservation. A
        // reservation ends only when ts-auth lists its invitation with a stored decision (accepted, declined or revoked);
        // ts-auth derives expired from its clock without storing it, and that invitation can still be accepted:
        // its recorded id or, for a lost response, an invitation for its email and target that it did not know and
        // that no other reservation records. Each listed invitation ends at most one reservation, within this
        // compare-and-set, so the reservations left for an email cover every request for it that may still commit.
        private static void Reconcile(OrganizationRecord org, IReadOnlyList<TsAuthPlatformInvitation> invitations)
        {
            foreach (var key in org.InvitationReservations.Keys.ToArray())
            {
                var reservation = org.InvitationReservations[key];
                var recorded = org.InvitationReservations.Values.Select(other => other.InvitationId).ToHashSet(StringComparer.Ordinal);
                var invitation = invitations.FirstOrDefault(invitation => invitation.Status is "accepted" or "declined" or "revoked" && (reservation.InvitationId.Length > 0
                    ? invitation.Id == reservation.InvitationId
                    : Same(invitation, reservation) && !reservation.Known.Contains(invitation.Id) && !recorded.Contains(invitation.Id)));
                if (invitation is null)
                {
                    continue;
                }

                org.InvitationReservations.Remove(key);
                foreach (var (other, value) in org.InvitationReservations.ToArray())
                {
                    if (value.Email == reservation.Email && value.ProjectId == reservation.ProjectId)
                    {
                        org.InvitationReservations[other] = value with { Known = [.. value.Known, invitation.Id] };
                    }
                }
            }
        }

        // ts-auth holds one pending invitation per email and target: the organization, or one of its projects.
        private static bool Same(TsAuthPlatformInvitation invitation, InvitationReservation reservation)
        {
            return invitation.Email.Equals(reservation.Email, StringComparison.OrdinalIgnoreCase) && invitation.ProjectId == reservation.ProjectId;
        }

        // Occupied slots are distinct emails across members, ts-auth's pending and expired invitations (both still
        // stored pending, so either can be accepted until a revoke is stored) and open reservations, so an invitee who
        // just accepted counts once. No clock decides anything here.
        // An email that already holds a reservation is invited again on that slot: ts-auth keeps one pending
        // invitation per email and target, so a retry and the request it replaces never both hold an invitation.
        private static void Capacity(OrganizationRecord org, IReadOnlyList<TsAuthPlatformMember> members, IReadOnlyList<TsAuthPlatformInvitation> invitations, string email)
        {
            if (org.DeletionRequested)
            {
                throw new AccountAccessException("Organization deletion has been requested.", 409);
            }

            var memberEmails = members.Select(member => member.Email.ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);
            var pending = invitations.Where(invitation => invitation.Status is "pending" or "expired").Select(invitation => invitation.Email.ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);
            var reserved = org.InvitationReservations.Values.Select(reservation => reservation.Email).ToHashSet(StringComparer.Ordinal);
            if (memberEmails.Contains(email))
            {
                throw new AccountAccessException("This email is already an active member.", 409);
            }

            if (pending.Contains(email))
            {
                throw new AccountAccessException("This email already has an open invitation; resend or revoke it.", 409);
            }

            var plan = PlanCatalog.For(org)
                ?? throw new AccountAccessException("The organization's member allowance is unavailable.", 409);
            var limit = PlanCatalog.ResourceLimits(org).Members;
            if (limit > 0 && memberEmails.Union(pending).Union(reserved).Append(email).Distinct(StringComparer.Ordinal).Count() > limit)
            {
                throw new AccountAccessException($"The {plan.Name} plan allows {limit} members including pending invitations.", 409);
            }
        }
    }
}

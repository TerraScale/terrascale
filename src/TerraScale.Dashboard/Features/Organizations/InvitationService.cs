using System.Net.Mail;
using TerraScale.Dashboard.Common.Formatting;
using TerraScale.Dashboard.Errors;
using TerraScale.Dashboard.Features.Auth;
using TerraScale.Dashboard.Platform.TsAuth;

namespace TerraScale.Dashboard.Features.Organizations
{

    public sealed record PendingInvitation(OrganizationRecord Organization, InvitationRecord Invitation, TsAuthPlatformNamed? Project = null);

    public readonly union InvitationResponse(PendingInvitation, DashboardValidation, DashboardNotFound, DashboardForbidden);

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
            var reservation = AccountService.NewId();
            await accounts.UpdateOrganizationAsync(context, async org =>
            {
                OrganizationOperations.Manage(org, context.UserId);
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
                await ReleaseAsync(context, reservation);
                throw;
            }
            try
            {
                await accounts.UpdateOrganizationAsync(context, org =>
                {
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
            OrganizationOperations.AvailableRole(remote.Role);
            if (remote.ProjectId is { } projectId && (!TerraScaleIds.IsValid(projectId) ||
                !(await platform.ListProjectsAsync(context.Session, context.Organization!.Id, cancellationToken)).Any(project => project.Id == projectId)))
            {
                throw new AccountAccessException("This invitation's project is unavailable, so it cannot be resent.", 409);
            }
            await access.RevokeInvitationAsync(context, id, cancellationToken);
            return await CreateAsync(context, remote.Email, remote.Role, remote.ProjectId, cancellationToken);
        }

        public async Task RevokeAsync(AccountContext context, string id, CancellationToken cancellationToken)
        {
            context = await FreshAsync(context, cancellationToken);
            await access.RevokeInvitationAsync(context, id, cancellationToken);
        }

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

        public async Task<InvitationResponse> RespondAsync(SessionRecord session, string token, string action, CancellationToken cancellationToken)
        {
            action = action.Trim().ToLowerInvariant();
            if (action is not ("accept" or "decline"))
            {
                return new DashboardValidation("Choose whether to accept or decline the invitation.");
            }

            var pending = await FindAsync(session, token, cancellationToken);
            if (pending is null)
            {
                return new DashboardNotFound("Invitation is unavailable.");
            }

            if (!pending.Invitation.Email.Equals(session.Email, StringComparison.OrdinalIgnoreCase))
            {
                return new DashboardForbidden("Sign in with the invited email to answer this invitation.");
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

        private static bool Same(TsAuthPlatformInvitation invitation, InvitationReservation reservation)
        {
            return invitation.Email.Equals(reservation.Email, StringComparison.OrdinalIgnoreCase) && invitation.ProjectId == reservation.ProjectId;
        }

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
                throw new AccountAccessException($"The {plan.Name} plan allows {ValueFormat.Plural(limit, "member")} including pending invitations.", 409);
            }
        }
    }
}

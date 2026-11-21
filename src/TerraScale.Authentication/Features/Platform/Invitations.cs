using System.Buffers;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using System.Diagnostics;
using TerraScale.Authentication.Configuration;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Errors;
using TerraScale.Authentication.Features.Email;
using TerraScale.Authentication.Features.Identity;
using TerraScale.Authentication.Features.RateLimiting;
using TerraScale.Authentication.Features.SignIn;
using TerraScale.Authentication.Security;
using TerraScale.Authentication.Storage;

namespace TerraScale.Authentication.Features.Platform;

public readonly record struct InvitationIssued(InvitationRecord Invitation, string Token, string OrganizationName);

public readonly union InvitationCreate(InvitationIssued, Failure);

public readonly union InvitationPage(Page<InvitationRecord>, Failure);

public readonly union InvitationRevoke(Done, Failure);

public readonly record struct InvitationView(InvitationRecord Invitation, string OrganizationName, string? ProjectName);

public readonly union InvitationLookup(InvitationView, Failure);

public readonly union InvitationAccept(InvitationRecord, Failure);

public readonly union InvitationDecline(Done, Failure);

internal readonly record struct InvitationAuthority(IReadOnlyList<Change> Authority, string OrganizationName);

internal readonly union AuthorityCheck(InvitationAuthority, Failure);

public sealed class Invitations(AuthData data, PlatformDirectory directory, Users users, SignInMail mail, AttemptLimiter limiter, AuthOptions options, TimeProvider clock)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(7);

    public const int PerOrganizationPerHour = 100;

    private const string Instance = PlatformDirectory.Instance;
    private const string Prefix = "tsi_";
    private const string Alphabet = "0123456789abcdefghjkmnpqrstvwxyz";
    private static readonly SearchValues<char> Base32 = SearchValues.Create(Alphabet);

    public static bool IsToken(string? value) =>
        value is { Length: 56 } && value.StartsWith(Prefix, StringComparison.Ordinal) && value.AsSpan(Prefix.Length).IndexOfAnyExcept(Base32) < 0;

    public string Status(InvitationRecord invitation)
    {
        ArgumentNullException.ThrowIfNull(invitation);
        return invitation.Status == "pending" && invitation.ExpiresAt <= clock.GetUtcNow() ? "expired" : invitation.Status;
    }

    public async Task<InvitationCreate> CreateAsync(UserRecord caller, string organizationId, string email, string role, string? projectId, string locale, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        var precheck = await this.AuthorizeAsync(caller.Id, organizationId, projectId, role, cancellationToken).ConfigureAwait(false);
        if (precheck is Failure precheckFailure)
        {
            return precheckFailure;
        }

        if (!await limiter.TryAcquireAsync(Instance, "invitation", organizationId, PerOrganizationPerHour, TimeSpan.FromHours(1), cancellationToken).ConfigureAwait(false))
        {
            return (Failure)new QuotaExceeded("This organization sent too many invitations; retry within the hour.", "rate_limited");
        }

        // Hold capacity across the commit: a known-full queue must never leave an undeliverable pending invite.
        // Dispose releases it on every failed authorization, conflict, cancellation or transaction failure.
        var dashboard = options.DashboardClient;
        using var reservation = dashboard is not null ? mail.Reserve() : null;
        cancellationToken.ThrowIfCancellationRequested();
        return await directory.CommitAsync(async () =>
        {
            var authorized = await this.AuthorizeAsync(caller.Id, organizationId, projectId, role, cancellationToken).ConfigureAwait(false);
            if (authorized is not InvitationAuthority authority)
            {
                return PlatformDirectory.Refused<InvitationIssued>(PlatformDirectory.RefusalOf(authorized));
            }

            var held = await this.HeldAccessAsync(organizationId, projectId, email, role, cancellationToken).ConfigureAwait(false);
            if (held is { } already)
            {
                return PlatformDirectory.Refused<InvitationIssued>(already);
            }

            if (await directory.FencedAsync(organizationId, projectId, null, cancellationToken).ConfigureAwait(false))
            {
                return PlatformDirectory.Refused<InvitationIssued>(PlatformDirectory.UnavailableFailure());
            }

            var now = clock.GetUtcNow();
            var token = Prefix + RandomNumberGenerator.GetString(Alphabet, 52);
            var invitation = new InvitationRecord(AuthIds.New(), Instance, organizationId, projectId, email, role, caller.Id, now, now + Lifetime, "pending", Tokens.Sha256Hex(token));
            List<Change> operations = [.. authority.Authority];
            if (await this.PendingAsync(organizationId, projectId, email, cancellationToken).ConfigureAwait(false) is { } earlier)
            {
                if (this.Status(earlier.Value) == "pending")
                {
                    return PlatformDirectory.Refused<InvitationIssued>(PendingConflict());
                }

                operations.Add(Change.Update(earlier, earlier.Value with { Status = "expired" }));
            }

            operations.Add(Change.Insert(invitation));
            operations.Add(directory.Audit(organizationId, caller.Id, "invitation.created", email, projectId is null ? role : $"{projectId}/{role}"));
            return new PlatformDirectory.Plan<InvitationIssued>(operations, new InvitationIssued(invitation, token, authority.OrganizationName),
                async probe => await this.PendingAsync(organizationId, projectId, email, probe).ConfigureAwait(false) is { } later && this.Status(later.Value) == "pending",
                PendingConflict());
        }, cancellationToken).ConfigureAwait(false) switch
        {
            InvitationIssued issued => await DeliverAsync(caller, organizationId, email, locale, issued, reservation, dashboard).ConfigureAwait(false),
            Failure failure => failure,
        };
    }

    private static async Task<InvitationCreate> DeliverAsync(UserRecord caller, string organizationId, string email, string locale, InvitationIssued issued,
        SignInMail.Reservation? reservation, DashboardClientOptions? dashboard)
    {
        var (invitation, token, organizationName) = (issued.Invitation, issued.Token, issued.OrganizationName);
        // The dashboard serves the accept page at /invitations/{token} on the origin of its first sign-in callback.
        if (dashboard is not null)
        {
            await reservation!.EnqueueEmailAsync(AuthEmailKind.Invitation, new AuthEmailValues(invitation.Email)
            {
                Link = new Uri($"{dashboard.RedirectUris[0].GetLeftPart(UriPartial.Authority)}/invitations/{token}"),
                ExpiresIn = Lifetime,
                Organization = organizationName,
                Tenant = organizationId,
                Inviter = caller.Email,
            }, locale).ConfigureAwait(false);
        }

        return new InvitationIssued(invitation, token, organizationName);
    }

    private async Task<AuthorityCheck> AuthorizeAsync(string callerId, string organizationId, string? projectId, string role, CancellationToken cancellationToken)
    {
        if (projectId is null)
        {
            var read = await directory.OrganizationAccessAsync(callerId, organizationId, cancellationToken).ConfigureAwait(false);
            if (read is not OrganizationAccess access)
            {
                return PlatformDirectory.RefusalOf(read);
            }

            if (PlatformDirectory.RoleRefusal(access, role == Roles.Owner ? Roles.Owner : Roles.Admin) is { } forbidden)
            {
                return forbidden;
            }

            return new InvitationAuthority([PlatformDirectory.Authority(access), Change.Assert(access.Organization)], access.Organization.Value.Name);
        }

        var projectRead = await directory.ProjectAccessAsync(callerId, organizationId, projectId, cancellationToken).ConfigureAwait(false);
        if (projectRead is not ProjectAccess project)
        {
            return PlatformDirectory.RefusalOf(projectRead);
        }

        if (PlatformDirectory.RoleRefusal(project, Roles.Admin) is { } projectForbidden)
        {
            return projectForbidden;
        }

        return new InvitationAuthority([PlatformDirectory.Authority(project, Roles.Admin), Change.Assert(project.Organization), Change.Assert(project.Project)], project.Organization.Value.Name);
    }

    private async Task<Conflict?> HeldAccessAsync(string organizationId, string? projectId, string email, string role, CancellationToken cancellationToken)
    {
        if (await users.FindByEmailAsync(Instance, email, cancellationToken).ConfigureAwait(false) is not { } invitee)
        {
            return null;
        }

        if (projectId is null)
        {
            return await directory.OrganizationAccessAsync(invitee.Id, organizationId, cancellationToken).ConfigureAwait(false) switch
            {
                OrganizationAccess { Role: { } } => AlreadyMember(),
                OrganizationAccess => null,
                Failure failure => failure is NotFound ? null : throw PlatformDirectory.ToException(failure),
            };
        }

        return await directory.ProjectAccessAsync(invitee.Id, organizationId, projectId, cancellationToken).ConfigureAwait(false) switch
        {
            ProjectAccess access => Roles.Rank(access.Role) >= Roles.Rank(role) ? AlreadyMember() : null,
            Failure failure => failure is NotFound ? null : throw PlatformDirectory.ToException(failure),
        };
    }

    private static Conflict AlreadyMember() => new("That address already has this access.", "already_member");

    public async Task<InvitationPage> ListAsync(string userId, string organizationId, string? projectId, int limit, string? after, CancellationToken cancellationToken)
    {
        Failure? refusal = projectId is null
            ? await directory.OrganizationAccessAsync(userId, organizationId, cancellationToken).ConfigureAwait(false) switch
            {
                OrganizationAccess access => PlatformDirectory.RoleRefusal(access, Roles.Admin),
                Failure failure => failure,
            }
            : await directory.ProjectAccessAsync(userId, organizationId, projectId, cancellationToken).ConfigureAwait(false) switch
            {
                ProjectAccess access => PlatformDirectory.RoleRefusal(access, Roles.Admin),
                Failure failure => failure,
            };
        if (refusal is { } denied)
        {
            return denied;
        }

        var from = PlatformDirectory.Cursor(after);
        var rows = await data.ReadAsync((db, token) => db.Invitations
            .Where(item => item.OrganizationId == organizationId && (projectId == null || item.ProjectId == projectId) && string.Compare(item.Id, from) > 0)
            .OrderBy(item => item.Id).Take(limit + 1).ToListAsync(token), cancellationToken).ConfigureAwait(false);
        return PlatformDirectory.Paged(rows, limit, item => item.Id);
    }

    public async Task<InvitationRevoke> RevokeAsync(string userId, string organizationId, string invitationId, CancellationToken cancellationToken) =>
        await directory.CommitAsync(async () =>
        {
            if (!PlatformDirectory.IsId(invitationId) || await this.ReadAsync(invitationId, cancellationToken).ConfigureAwait(false) is not { } invitation
                || invitation.Value.OrganizationId != organizationId)
            {
                return PlatformDirectory.Refused<Done>(PlatformDirectory.NotFoundFailure());
            }

            Change authority;
            if (invitation.Value.ProjectId is { } projectId)
            {
                var projectRead = await directory.ProjectAccessAsync(userId, organizationId, projectId, cancellationToken).ConfigureAwait(false);
                if (projectRead is not ProjectAccess projectAccess)
                {
                    return PlatformDirectory.Refused<Done>(PlatformDirectory.RefusalOf(projectRead));
                }

                if (PlatformDirectory.RoleRefusal(projectAccess, Roles.Admin) is { } projectForbidden)
                {
                    return PlatformDirectory.Refused<Done>(projectForbidden);
                }

                authority = PlatformDirectory.Authority(projectAccess, Roles.Admin);
            }
            else
            {
                var orgRead = await directory.OrganizationAccessAsync(userId, organizationId, cancellationToken).ConfigureAwait(false);
                if (orgRead is not OrganizationAccess orgAccess)
                {
                    return PlatformDirectory.Refused<Done>(PlatformDirectory.RefusalOf(orgRead));
                }

                if (PlatformDirectory.RoleRefusal(orgAccess, Roles.Admin) is { } orgForbidden)
                {
                    return PlatformDirectory.Refused<Done>(orgForbidden);
                }

                authority = PlatformDirectory.Authority(orgAccess);
            }

            if (invitation.Value.Status != "pending")
            {
                return PlatformDirectory.Refused<Done>(new Conflict("The invitation is no longer pending.", "not_pending"));
            }

            List<Change> operations = [authority, this.Close(invitation, "revoked", userId)];
            operations.Add(directory.Audit(organizationId, userId, "invitation.revoked", invitation.Value.Email));
            return new PlatformDirectory.Plan<Done>(operations, new Done());
        }, cancellationToken).ConfigureAwait(false) switch
        {
            Done done => done,
            Failure failure => failure,
        };

    public async Task<InvitationLookup> LookupAsync(string token, CancellationToken cancellationToken)
    {
        var invitation = await this.ByTokenAsync(token, cancellationToken).ConfigureAwait(false);
        if (invitation is null || this.Status(invitation.Value) != "pending"
            || await directory.OrganizationAsync(invitation.Value.OrganizationId, cancellationToken).ConfigureAwait(false) is not { } organization)
        {
            return (Failure)new NotFound("No such resource.");
        }

        string? projectName = null;
        if (invitation.Value.ProjectId is { } projectId)
        {
            if (await directory.ProjectAsync(projectId, cancellationToken).ConfigureAwait(false) is not { } project)
            {
                return (Failure)new NotFound("No such resource.");
            }

            projectName = project.Value.Name;
        }

        return new InvitationView(invitation.Value, organization.Value.Name, projectName);
    }

    public async Task<InvitationAccept> AcceptAsync(UserRecord caller, string token, CancellationToken cancellationToken) =>
        await directory.CommitAsync(async () =>
        {
            var decidable = await this.DecidableAsync(caller, token, cancellationToken).ConfigureAwait(false);
            if (decidable is not Versioned<InvitationRecord> invitation)
            {
                return PlatformDirectory.Refused<InvitationRecord>(PlatformDirectory.RefusalOf(decidable));
            }

            var record = invitation.Value;
            List<Change> operations;
            if (record.ProjectId is { } projectId)
            {
                if (await directory.GrantOperationsAsync(projectId, caller.Id, record.Role, refuseHeld: true, cancellationToken).ConfigureAwait(false) is not { } granted)
                {
                    return PlatformDirectory.Refused<InvitationRecord>(PlatformDirectory.NotFoundFailure());
                }

                operations = granted;
                operations.Add(directory.Audit(record.OrganizationId, caller.Id, "project_member.role_set", caller.Id, $"{projectId}/{record.Role}"));
            }
            else
            {
                if (await directory.OrganizationAsync(record.OrganizationId, cancellationToken).ConfigureAwait(false) is null)
                {
                    return PlatformDirectory.Refused<InvitationRecord>(PlatformDirectory.NotFoundFailure());
                }

                if (await directory.JoinAsync(record.OrganizationId, caller.Id, record.Role, cancellationToken).ConfigureAwait(false) is not { } joined)
                {
                    return PlatformDirectory.Refused<InvitationRecord>(new Conflict("You are already a member of this organization.", "already_member"));
                }

                operations = joined;
                operations.Add(directory.Audit(record.OrganizationId, caller.Id, "member.joined", caller.Id, record.Role));
            }

            operations.Add(this.Close(invitation, "accepted", caller.Id));
            operations.Add(directory.Audit(record.OrganizationId, caller.Id, "invitation.accepted", record.Email));
            return new PlatformDirectory.Plan<InvitationRecord>(operations, record);
        }, cancellationToken).ConfigureAwait(false) switch
        {
            InvitationRecord accepted => accepted,
            Failure failure => failure,
        };

    public async Task<InvitationDecline> DeclineAsync(UserRecord caller, string token, CancellationToken cancellationToken) =>
        await directory.CommitAsync(async () =>
        {
            var decidable = await this.DecidableAsync(caller, token, cancellationToken).ConfigureAwait(false);
            if (decidable is not Versioned<InvitationRecord> invitation)
            {
                return PlatformDirectory.Refused<Done>(PlatformDirectory.RefusalOf(decidable));
            }

            List<Change> operations = [this.Close(invitation, "declined", caller.Id)];
            operations.Add(directory.Audit(invitation.Value.OrganizationId, caller.Id, "invitation.declined", invitation.Value.Email));
            return new PlatformDirectory.Plan<Done>(operations, new Done());
        }, cancellationToken).ConfigureAwait(false) switch
        {
            Done done => done,
            Failure failure => failure,
        };

    private static Conflict PendingConflict() =>
        new("An invitation for this address is already pending.", "invitation_pending");

    private Task<Versioned<InvitationRecord>?> PendingAsync(string organizationId, string? projectId, string email, CancellationToken cancellationToken) =>
        data.ReadAsync((db, token) => db.Invitations.Where(item => item.OrganizationId == organizationId && item.ProjectId == projectId && item.Email == email && item.Status == "pending")
            .WithVersion().SingleOrDefaultAsync(token), cancellationToken);

    internal readonly union Decidable(Versioned<InvitationRecord>, Failure);

    private async Task<Decidable> DecidableAsync(UserRecord caller, string token, CancellationToken cancellationToken)
    {
        var invitation = await this.ByTokenAsync(token, cancellationToken).ConfigureAwait(false);
        if (invitation is null)
        {
            return (Failure)new NotFound("No such resource.");
        }

        if (!string.Equals(invitation.Value.Email, caller.Email, StringComparison.Ordinal))
        {
            return (Failure)new Forbidden("The invitation is for another email address.", "email_mismatch");
        }

        return this.Status(invitation.Value) switch
        {
            "pending" => invitation,
            "expired" => (Failure)new Conflict("The invitation has expired.", "invitation_expired"),
            _ => (Failure)new Conflict("The invitation is no longer pending.", "not_pending"),
        };
    }

    private Change Close(Versioned<InvitationRecord> invitation, string status, string userId) =>
        Change.Update(invitation, invitation.Value with { Status = status, DecidedAt = clock.GetUtcNow(), DecidedBy = userId });

    private async Task<Versioned<InvitationRecord>?> ByTokenAsync(string token, CancellationToken cancellationToken)
    {
        if (!IsToken(token))
        {
            return null;
        }

        var digest = Tokens.Sha256Hex(token);
        return await data.ReadAsync((db, probe) => db.Invitations.Where(item => item.TokenHash == digest && item.InstanceId == Instance).WithVersion().SingleOrDefaultAsync(probe),
            cancellationToken).ConfigureAwait(false) is { } invitation && Tokens.FixedTimeEquals(invitation.Value.TokenHash, digest)
            ? invitation
            : null;
    }

    private Task<Versioned<InvitationRecord>?> ReadAsync(string invitationId, CancellationToken cancellationToken) =>
        data.ReadAsync((db, token) => db.Invitations.Where(item => item.Id == invitationId && item.InstanceId == Instance).WithVersion().SingleOrDefaultAsync(token), cancellationToken);
}

using System.Buffers;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using TerraScale.Authentication.Configuration;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Email;
using TerraScale.Authentication.Features.Identity;
using TerraScale.Authentication.Features.RateLimiting;
using TerraScale.Authentication.Features.SignIn;
using TerraScale.Authentication.Security;
using TerraScale.Authentication.Storage;

namespace TerraScale.Authentication.Features.Platform;

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

    public async Task<(InvitationRecord Invitation, string Token)> CreateAsync(UserRecord caller, string organizationId, string email, string role, string? projectId, string locale, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        async Task<(Change[] Authority, string OrganizationName)> AuthorizeAsync()
        {
            if (projectId is null)
            {
                var access = await directory.OrganizationAccessAsync(caller.Id, organizationId, cancellationToken).ConfigureAwait(false);
                PlatformDirectory.RequireRole(access, role == Roles.Owner ? Roles.Owner : Roles.Admin);
                return ([PlatformDirectory.Authority(access), Change.Assert(access.Organization)], access.Organization.Value.Name);
            }

            var project = await directory.ProjectAccessAsync(caller.Id, organizationId, projectId, cancellationToken).ConfigureAwait(false);
            PlatformDirectory.RequireRole(project, Roles.Admin);
            return ([PlatformDirectory.Authority(project, Roles.Admin), Change.Assert(project.Organization), Change.Assert(project.Project)], project.Organization.Value.Name);
        }

        // Checked before charging, so only those who may invite spend the organization's budget; the commit checks again.
        _ = await AuthorizeAsync().ConfigureAwait(false);
        if (!await limiter.TryAcquireAsync(Instance, "invitation", organizationId, PerOrganizationPerHour, TimeSpan.FromHours(1), cancellationToken).ConfigureAwait(false))
        {
            throw new ApiException(StatusCodes.Status429TooManyRequests, "rate_limited", "This organization sent too many invitations; retry within the hour.");
        }

        // Hold capacity across the commit: a known-full queue must never leave an undeliverable pending invite.
        // Dispose releases it on every failed authorization, conflict, cancellation or transaction failure.
        var dashboard = options.DashboardClient;
        using var reservation = dashboard is not null ? mail.Reserve() : null;
        cancellationToken.ThrowIfCancellationRequested();
        var (invitation, token, organizationName) = await directory.CommitAsync(async () =>
        {
            var (authority, organizationName) = await AuthorizeAsync().ConfigureAwait(false);
            if (await this.AlreadyHasAsync(organizationId, projectId, email, role, cancellationToken).ConfigureAwait(false))
            {
                throw PlatformDirectory.Conflict("already_member", "That address already has this access.");
            }

            await directory.FenceAsync(organizationId, projectId, null, cancellationToken).ConfigureAwait(false);
            var now = clock.GetUtcNow();
            var token = Prefix + RandomNumberGenerator.GetString(Alphabet, 52);
            var invitation = new InvitationRecord(AuthIds.New(), Instance, organizationId, projectId, email, role, caller.Id, now, now + Lifetime, "pending", Tokens.Sha256Hex(token));
            List<Change> operations = [.. authority];
            if (await this.PendingAsync(organizationId, projectId, email, cancellationToken).ConfigureAwait(false) is { } earlier)
            {
                if (this.Status(earlier.Value) == "pending")
                {
                    throw PendingConflict();
                }

                operations.Add(Change.Update(earlier, earlier.Value with { Status = "expired" }));
            }

            operations.Add(Change.Insert(invitation));
            operations.Add(directory.Audit(organizationId, caller.Id, "invitation.created", email, projectId is null ? role : $"{projectId}/{role}"));
            return new PlatformDirectory.Plan<(InvitationRecord, string, string)>(operations, (invitation, token, organizationName),
                async probe => await this.PendingAsync(organizationId, projectId, email, probe).ConfigureAwait(false) is { } held && this.Status(held.Value) == "pending",
                PendingConflict());
        }, cancellationToken).ConfigureAwait(false);

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
            }, locale);
        }

        return (invitation, token);
    }

    public async Task<Page<InvitationRecord>> ListAsync(string userId, string organizationId, string? projectId, int limit, string? after, CancellationToken cancellationToken)
    {
        if (projectId is null)
        {
            PlatformDirectory.RequireRole(await directory.OrganizationAccessAsync(userId, organizationId, cancellationToken).ConfigureAwait(false), Roles.Admin);
        }
        else
        {
            PlatformDirectory.RequireRole(await directory.ProjectAccessAsync(userId, organizationId, projectId, cancellationToken).ConfigureAwait(false), Roles.Admin);
        }

        var from = PlatformDirectory.Cursor(after);
        var rows = await data.ReadAsync((db, token) => db.Invitations
            .Where(item => item.OrganizationId == organizationId && (projectId == null || item.ProjectId == projectId) && string.Compare(item.Id, from) > 0)
            .OrderBy(item => item.Id).Take(limit + 1).ToListAsync(token), cancellationToken).ConfigureAwait(false);
        return PlatformDirectory.Paged(rows, limit, item => item.Id);
    }

    public Task RevokeAsync(string userId, string organizationId, string invitationId, CancellationToken cancellationToken) =>
        directory.CommitAsync(async () =>
        {
            if (!PlatformDirectory.IsId(invitationId) || await this.ReadAsync(invitationId, cancellationToken).ConfigureAwait(false) is not { } invitation
                || invitation.Value.OrganizationId != organizationId)
            {
                throw PlatformDirectory.NotFound();
            }

            Change authority;
            if (invitation.Value.ProjectId is { } projectId)
            {
                var access = await directory.ProjectAccessAsync(userId, organizationId, projectId, cancellationToken).ConfigureAwait(false);
                PlatformDirectory.RequireRole(access, Roles.Admin);
                authority = PlatformDirectory.Authority(access, Roles.Admin);
            }
            else
            {
                var access = await directory.OrganizationAccessAsync(userId, organizationId, cancellationToken).ConfigureAwait(false);
                PlatformDirectory.RequireRole(access, Roles.Admin);
                authority = PlatformDirectory.Authority(access);
            }

            if (invitation.Value.Status != "pending")
            {
                throw PlatformDirectory.Conflict("not_pending", "The invitation is no longer pending.");
            }

            List<Change> operations = [authority, this.Close(invitation, "revoked", userId)];
            operations.Add(directory.Audit(organizationId, userId, "invitation.revoked", invitation.Value.Email));
            return new PlatformDirectory.Plan<bool>(operations, true);
        }, cancellationToken);

    public async Task<(InvitationRecord Invitation, string OrganizationName, string? ProjectName)> LookupAsync(string token, CancellationToken cancellationToken)
    {
        var invitation = await this.ByTokenAsync(token, cancellationToken).ConfigureAwait(false);
        if (invitation is null || this.Status(invitation.Value) != "pending"
            || await directory.OrganizationAsync(invitation.Value.OrganizationId, cancellationToken).ConfigureAwait(false) is not { } organization)
        {
            throw PlatformDirectory.NotFound();
        }

        string? projectName = null;
        if (invitation.Value.ProjectId is { } projectId)
        {
            projectName = (await directory.ProjectAsync(projectId, cancellationToken).ConfigureAwait(false) ?? throw PlatformDirectory.NotFound()).Value.Name;
        }

        return (invitation.Value, organization.Value.Name, projectName);
    }

    public Task<InvitationRecord> AcceptAsync(UserRecord caller, string token, CancellationToken cancellationToken) =>
        directory.CommitAsync(async () =>
        {
            var invitation = await this.DecidableAsync(caller, token, cancellationToken).ConfigureAwait(false);
            var record = invitation.Value;
            List<Change> operations;
            if (record.ProjectId is { } projectId)
            {
                operations = await directory.GrantOperationsAsync(projectId, caller.Id, record.Role, refuseHeld: true, cancellationToken).ConfigureAwait(false) ?? throw PlatformDirectory.NotFound();
                operations.Add(directory.Audit(record.OrganizationId, caller.Id, "project_member.role_set", caller.Id, $"{projectId}/{record.Role}"));
            }
            else
            {
                if (await directory.OrganizationAsync(record.OrganizationId, cancellationToken).ConfigureAwait(false) is null)
                {
                    throw PlatformDirectory.NotFound();
                }

                operations = await directory.JoinAsync(record.OrganizationId, caller.Id, record.Role, cancellationToken).ConfigureAwait(false)
                    ?? throw PlatformDirectory.Conflict("already_member", "You are already a member of this organization.");
                operations.Add(directory.Audit(record.OrganizationId, caller.Id, "member.joined", caller.Id, record.Role));
            }

            operations.Add(this.Close(invitation, "accepted", caller.Id));
            operations.Add(directory.Audit(record.OrganizationId, caller.Id, "invitation.accepted", record.Email));
            return new PlatformDirectory.Plan<InvitationRecord>(operations, record);
        }, cancellationToken);

    public Task DeclineAsync(UserRecord caller, string token, CancellationToken cancellationToken) =>
        directory.CommitAsync(async () =>
        {
            var invitation = await this.DecidableAsync(caller, token, cancellationToken).ConfigureAwait(false);
            List<Change> operations = [this.Close(invitation, "declined", caller.Id)];
            operations.Add(directory.Audit(invitation.Value.OrganizationId, caller.Id, "invitation.declined", invitation.Value.Email));
            return new PlatformDirectory.Plan<bool>(operations, true);
        }, cancellationToken);

    private static ApiException PendingConflict() =>
        PlatformDirectory.Conflict("invitation_pending", "An invitation for this address is already pending.");

    private Task<Versioned<InvitationRecord>?> PendingAsync(string organizationId, string? projectId, string email, CancellationToken cancellationToken) =>
        data.ReadAsync((db, token) => db.Invitations.Where(item => item.OrganizationId == organizationId && item.ProjectId == projectId && item.Email == email && item.Status == "pending")
            .WithVersion().SingleOrDefaultAsync(token), cancellationToken);

    private async Task<Versioned<InvitationRecord>> DecidableAsync(UserRecord caller, string token, CancellationToken cancellationToken)
    {
        var invitation = await this.ByTokenAsync(token, cancellationToken).ConfigureAwait(false) ?? throw PlatformDirectory.NotFound();
        if (!string.Equals(invitation.Value.Email, caller.Email, StringComparison.Ordinal))
        {
            throw new ApiException(StatusCodes.Status403Forbidden, "email_mismatch", "The invitation is for another email address.");
        }

        return this.Status(invitation.Value) switch
        {
            "pending" => invitation,
            "expired" => throw PlatformDirectory.Conflict("invitation_expired", "The invitation has expired."),
            _ => throw PlatformDirectory.Conflict("not_pending", "The invitation is no longer pending."),
        };
    }

    private Change Close(Versioned<InvitationRecord> invitation, string status, string userId) =>
        Change.Update(invitation, invitation.Value with { Status = status, DecidedAt = clock.GetUtcNow(), DecidedBy = userId });

    private async Task<bool> AlreadyHasAsync(string organizationId, string? projectId, string email, string role, CancellationToken cancellationToken)
    {
        if (await users.FindByEmailAsync(Instance, email, cancellationToken).ConfigureAwait(false) is not { } invitee)
        {
            return false;
        }

        try
        {
            return projectId is null
                ? (await directory.OrganizationAccessAsync(invitee.Id, organizationId, cancellationToken).ConfigureAwait(false)).Role is not null
                : Roles.Rank((await directory.ProjectAccessAsync(invitee.Id, organizationId, projectId, cancellationToken).ConfigureAwait(false)).Role) >= Roles.Rank(role);
        }
        catch (ApiException error) when (error.Status == StatusCodes.Status404NotFound)
        {
            return false;
        }
    }

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

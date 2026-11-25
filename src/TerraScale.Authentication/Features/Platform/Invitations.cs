using Dapper;
using System.Buffers;
using System.Security.Cryptography;
using TerraScale.Authentication.Configuration;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Email;
using TerraScale.Authentication.Features.Identity;
using TerraScale.Authentication.Features.RateLimiting;
using TerraScale.Authentication.Features.SignIn;
using TerraScale.Authentication.Security;
using TerraScale.Authentication.Storage;

namespace TerraScale.Authentication.Features.Platform;

public sealed class Invitations(AuthData data, PlatformDirectory directory, Users users, SignInMail mail, AttemptLimiter limiter, EmailAdmission admission, AuthOptions options, TimeProvider clock)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(7);

    public const int PerOrganizationPerHour = 100;

    public const int PerUserPerDay = 50;

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
                var access = await directory.OrganizationAccessAsync(caller.Id, organizationId, cancellationToken);
                PlatformDirectory.RequireRole(access, role == Roles.Owner ? Roles.Owner : Roles.Admin);
                return ([PlatformDirectory.Authority(access), Change.Assert(access.Organization)], access.Organization.Value.Name);
            }

            var project = await directory.ProjectAccessAsync(caller.Id, organizationId, projectId, cancellationToken);
            PlatformDirectory.RequireRole(project, Roles.Admin);
            return ([PlatformDirectory.Authority(project, Roles.Admin), Change.Assert(project.Organization), Change.Assert(project.Project)], project.Organization.Value.Name);
        }

        _ = await AuthorizeAsync();
        if (!await limiter.TryAcquireAsync(Instance, "invitation", organizationId, PerOrganizationPerHour, TimeSpan.FromHours(1), cancellationToken))
        {
            throw new ApiException(StatusCodes.Status429TooManyRequests, "rate_limited", "This organization sent too many invitations; retry within the hour.");
        }

        if (await limiter.AcquireAsync(Instance, "invitation-sender", caller.Id, PerUserPerDay, TimeSpan.FromDays(1), cancellationToken) is { } senderWait)
        {
            throw ApiException.RateLimited("You sent too many invitations today; retry after the Retry-After delay.", senderWait);
        }

        var dashboard = options.DashboardClient;
        if (dashboard is not null && await admission.RefusalAsync(EmailPurpose.Invitation, organizationId, cancellationToken) is { } emailWait)
        {
            throw ApiException.RateLimited("This organization's invitation email capacity is used up; retry after the Retry-After delay.", emailWait);
        }

        using var reservation = dashboard is not null ? mail.Reserve() : null;
        cancellationToken.ThrowIfCancellationRequested();
        var (invitation, token, organizationName) = await directory.CommitAsync(async () =>
        {
            var (authority, organizationName) = await AuthorizeAsync();
            if (await this.AlreadyHasAsync(organizationId, projectId, email, role, cancellationToken))
            {
                throw PlatformDirectory.Conflict("already_member", "That address already has this access.");
            }

            await directory.FenceAsync(organizationId, projectId, null, cancellationToken);
            var now = clock.GetUtcNow();
            var token = Prefix + RandomNumberGenerator.GetString(Alphabet, 52);
            var invitation = new InvitationRecord(AuthIds.New(), Instance, organizationId, projectId, email, role, caller.Id, now, now + Lifetime, "pending", Tokens.Sha256Hex(token));
            List<Change> operations = [.. authority];
            if (await this.PendingAsync(organizationId, projectId, email, cancellationToken) is { } earlier)
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
                async probe => await this.PendingAsync(organizationId, projectId, email, probe) is { } held && this.Status(held.Value) == "pending",
                PendingConflict());
        }, cancellationToken);

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
            PlatformDirectory.RequireRole(await directory.OrganizationAccessAsync(userId, organizationId, cancellationToken), Roles.Admin);
        }
        else
        {
            PlatformDirectory.RequireRole(await directory.ProjectAccessAsync(userId, organizationId, projectId, cancellationToken), Roles.Admin);
        }

        var from = PlatformDirectory.Cursor(after);
        var take = limit + 1;
        var rows = (await data.ReadAsync(connection => connection.QueryAsync<InvitationRecord>(
            "SELECT * FROM invitations WHERE organization_id = @organizationId AND (@projectId IS NULL OR project_id = @projectId) AND id > @from ORDER BY id LIMIT @take",
            new { organizationId, projectId, from, take }), cancellationToken)).AsList();
        return PlatformDirectory.Paged(rows, limit, item => item.Id);
    }

    public Task RevokeAsync(string userId, string organizationId, string invitationId, CancellationToken cancellationToken) =>
        directory.CommitAsync(async () =>
        {
            if (!PlatformDirectory.IsId(invitationId) || await this.ReadAsync(invitationId, cancellationToken) is not { } invitation
                || invitation.Value.OrganizationId != organizationId)
            {
                throw PlatformDirectory.NotFound();
            }

            Change authority;
            if (invitation.Value.ProjectId is { } projectId)
            {
                var access = await directory.ProjectAccessAsync(userId, organizationId, projectId, cancellationToken);
                PlatformDirectory.RequireRole(access, Roles.Admin);
                authority = PlatformDirectory.Authority(access, Roles.Admin);
            }
            else
            {
                var access = await directory.OrganizationAccessAsync(userId, organizationId, cancellationToken);
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
        var invitation = await this.ByTokenAsync(token, cancellationToken);
        if (invitation is null || this.Status(invitation.Value) != "pending"
            || await directory.OrganizationAsync(invitation.Value.OrganizationId, cancellationToken) is not { } organization)
        {
            throw PlatformDirectory.NotFound();
        }

        string? projectName = null;
        if (invitation.Value.ProjectId is { } projectId)
        {
            projectName = (await directory.ProjectAsync(projectId, cancellationToken) ?? throw PlatformDirectory.NotFound()).Value.Name;
        }

        return (invitation.Value, organization.Value.Name, projectName);
    }

    public Task<InvitationRecord> AcceptAsync(UserRecord caller, string token, CancellationToken cancellationToken) =>
        directory.CommitAsync(async () =>
        {
            var invitation = await this.DecidableAsync(caller, token, cancellationToken);
            var record = invitation.Value;
            List<Change> operations;
            if (record.ProjectId is { } projectId)
            {
                operations = await directory.GrantOperationsAsync(projectId, caller.Id, record.Role, refuseHeld: true, cancellationToken) ?? throw PlatformDirectory.NotFound();
                operations.Add(directory.Audit(record.OrganizationId, caller.Id, "project_member.role_set", caller.Id, $"{projectId}/{record.Role}"));
            }
            else
            {
                if (await directory.OrganizationAsync(record.OrganizationId, cancellationToken) is null)
                {
                    throw PlatformDirectory.NotFound();
                }

                operations = await directory.JoinAsync(record.OrganizationId, caller.Id, record.Role, cancellationToken)
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
            var invitation = await this.DecidableAsync(caller, token, cancellationToken);
            List<Change> operations = [this.Close(invitation, "declined", caller.Id)];
            operations.Add(directory.Audit(invitation.Value.OrganizationId, caller.Id, "invitation.declined", invitation.Value.Email));
            return new PlatformDirectory.Plan<bool>(operations, true);
        }, cancellationToken);

    private static ApiException PendingConflict() =>
        PlatformDirectory.Conflict("invitation_pending", "An invitation for this address is already pending.");

    private async Task<Versioned<InvitationRecord>?> PendingAsync(string organizationId, string? projectId, string email, CancellationToken cancellationToken) =>
        Versioned.Of(await data.ReadAsync(connection => connection.QuerySingleOrDefaultAsync<InvitationRecord>(
            "SELECT * FROM invitations WHERE organization_id = @organizationId AND project_id IS @projectId AND email = @email AND status = 'pending'",
            new { organizationId, projectId, email }), cancellationToken));

    private async Task<Versioned<InvitationRecord>> DecidableAsync(UserRecord caller, string token, CancellationToken cancellationToken)
    {
        var invitation = await this.ByTokenAsync(token, cancellationToken) ?? throw PlatformDirectory.NotFound();
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

    private async Task<bool> AlreadyHasAsync(string organizationId, string? projectId, string email, string role, CancellationToken cancellationToken) =>
        await users.FindByEmailAsync(Instance, email, cancellationToken) is { } invitee
        && await directory.HeldRoleAsync(invitee.Id, organizationId, projectId, cancellationToken) is { } held
        && (projectId is null || Roles.Rank(held) >= Roles.Rank(role));

    private async Task<Versioned<InvitationRecord>?> ByTokenAsync(string token, CancellationToken cancellationToken)
    {
        if (!IsToken(token))
        {
            return null;
        }

        var digest = Tokens.Sha256Hex(token);
        return Versioned.Of(await data.ReadAsync(connection => connection.QuerySingleOrDefaultAsync<InvitationRecord>(
            "SELECT * FROM invitations WHERE token_hash = @digest AND instance_id = @Instance", new { digest, Instance }), cancellationToken)) is { } invitation
            && Tokens.FixedTimeEquals(invitation.Value.TokenHash, digest)
            ? invitation
            : null;
    }

    private async Task<Versioned<InvitationRecord>?> ReadAsync(string invitationId, CancellationToken cancellationToken) =>
        Versioned.Of(await data.ReadAsync(connection => connection.QuerySingleOrDefaultAsync<InvitationRecord>(
            "SELECT * FROM invitations WHERE id = @invitationId AND instance_id = @Instance", new { invitationId, Instance }), cancellationToken));
}

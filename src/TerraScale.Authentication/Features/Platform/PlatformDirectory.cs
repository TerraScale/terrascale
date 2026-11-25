using Dapper;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Identity;
using TerraScale.Authentication.Features.Mfa;
using TerraScale.Authentication.Features.Oidc;
using TerraScale.Authentication.Features.WebAuthn;
using TerraScale.Authentication.Storage;
using TerraScale.DotNet;
using ZLogger;

namespace TerraScale.Authentication.Features.Platform;

public static class Roles
{
    public const string Viewer = "viewer";

    public const string Member = "member";

    public const string Admin = "admin";

    public const string Owner = "owner";

    public static IReadOnlyList<string> Organization { get; } = [Viewer, Member, Admin, Owner];

    public static IReadOnlyList<string> Project { get; } = [Viewer, Member, Admin];

    public static int Rank(string? role) => role switch { Viewer => 0, Member => 1, Admin => 2, Owner => 3, _ => -1 };

    public static string? Implied(string? organizationRole) => organizationRole == Owner ? Admin : organizationRole;

    public static string? Higher(string? first, string? second) => Rank(first) >= Rank(second) ? first : second;
}

public sealed record OrganizationAccess(Versioned<OrganizationRecord> Organization, Versioned<MembershipRecord> Membership)
{
    public string? Role => this.Membership.Value.Role;
}

public sealed record ProjectAccess(Versioned<MembershipRecord>? Membership, Versioned<OrganizationRecord> Organization, Versioned<ProjectRecord> Project, Versioned<ProjectMemberRecord>? Grant)
{
    public string Role => Roles.Higher(Roles.Implied(this.Membership?.Value.Role), this.Grant?.Value.Role)!;
}

public sealed record Page<T>(IReadOnlyList<T> Items, string? Next);

public sealed class PlatformDirectory(AuthData data, Users users, TotpCredentials mfa, PasskeyCredentials passkeys, TimeProvider clock, ILogger<PlatformDirectory> logger)
{
    public const string Instance = OidcIssuer.InstanceId;

    public const string MfaRequiredCode = "mfa_required";

    public static readonly TimeSpan TransferLifetime = TimeSpan.FromDays(7);

    private const int Attempts = 4;

    public static string? Name(string? value)
    {
        var trimmed = value?.Trim();
        return trimmed is { Length: >= 1 and <= 128 } && trimmed.Any(char.IsLetterOrDigit) && !trimmed.Any(char.IsControl) ? trimmed : null;
    }

    public static bool IsId(string? value) =>
        TerraScaleIds.IsValid(value);

    public async Task<OrganizationRecord> CreateOrganizationAsync(UserRecord user, string name, string? idempotencyKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        var id = idempotencyKey is null ? AuthIds.New() : AuthIds.Derived(Instance, "organization-create/" + user.Id, "key:" + idempotencyKey);
        await this.RefuseOrganizationCreationAsync(id, cancellationToken);
        await this.FenceAsync(id, null, user.Id, cancellationToken);
        if (idempotencyKey is not null && await this.OrganizationAsync(id, cancellationToken) is { } existing)
        {
            return existing.Value.CreatedBy == user.Id ? existing.Value : throw ApiException.Unavailable();
        }

        var now = clock.GetUtcNow();
        var organization = new OrganizationRecord(id, Instance, name, user.Id, now, Owners: 1, Projects: 0);
        var membership = new MembershipRecord(Instance, organization.Id, user.Id, Roles.Owner, now);
        await this.RefuseOrganizationCreationAsync(id, cancellationToken);
        await this.FenceAsync(id, null, user.Id, cancellationToken);
        if (await data.SaveAsync(
        [
            Change.Insert(organization),
            Change.Insert(membership),
            this.Audit(organization.Id, user.Id, "organization.created", null, name),
        ], cancellationToken))
        {
            return organization;
        }

        await this.RefuseOrganizationCreationAsync(id, cancellationToken);
        await this.FenceAsync(id, null, user.Id, cancellationToken);
        if (idempotencyKey is not null && await this.OrganizationAsync(id, cancellationToken) is { } winner && winner.Value.CreatedBy == user.Id)
        {
            return winner.Value;
        }

        throw ApiException.Unavailable();
    }

    public async Task<Page<(OrganizationRecord Organization, string? Role)>> OrganizationsAsync(string userId, int limit, string? after, CancellationToken cancellationToken)
    {
        var from = Cursor(after);
        var take = limit + 1;
        var memberships = (await data.ReadAsync(connection => connection.QueryAsync<MembershipRecord>(
            "SELECT * FROM memberships WHERE user_id = @userId AND organization_id > @from ORDER BY organization_id LIMIT @take", new { userId, from, take }), cancellationToken)).AsList();
        var ids = memberships.Select(membership => membership.OrganizationId).ToList();
        var found = (await data.ReadAsync(connection => connection.QueryAsync<OrganizationRecord>("SELECT * FROM organizations WHERE id IN @ids", new { ids }), cancellationToken))
            .ToDictionary(organization => organization.Id, StringComparer.Ordinal);
        var rows = memberships.Where(membership => found.ContainsKey(membership.OrganizationId)).Select(membership => (Organization: found[membership.OrganizationId], membership.Role)).ToList();
        var next = rows.Count > limit ? rows[limit - 1].Organization.Id : null;
        var organizations = new List<(OrganizationRecord, string?)>();
        foreach (var row in rows.Take(limit))
        {
            if (row.Role is not null || await this.CollaboratesAsync(userId, row.Organization.Id, null, cancellationToken))
            {
                organizations.Add((row.Organization, row.Role));
            }
        }

        return new(organizations, next);
    }

    public Task<OrganizationAccess> OrganizationAccessAsync(string userId, string organizationId, CancellationToken cancellationToken) =>
        this.OrganizationAccessAsync(userId, organizationId, requireMfa: true, cancellationToken);

    private async Task<OrganizationAccess> OrganizationAccessAsync(string userId, string organizationId, bool requireMfa, CancellationToken cancellationToken)
    {
        if (!IsId(organizationId))
        {
            throw NotFound();
        }

        var organization = await this.OrganizationAsync(organizationId, cancellationToken);
        var membership = organization is null ? null : await this.MembershipAsync(organizationId, userId, cancellationToken);
        if (organization is null || membership is null
            || membership.Value.Role is null && !await this.CollaboratesAsync(userId, organizationId, null, cancellationToken))
        {
            throw NotFound();
        }

        if (requireMfa)
        {
            await this.RequireMfaAsync(organization.Value, userId, cancellationToken);
        }

        return new(organization, membership);
    }

    public Task<OrganizationRecord> SetMfaPolicyAsync(string userId, string organizationId, bool require, CancellationToken cancellationToken) =>
        this.CommitAsync(async () =>
        {
            var access = await this.OrganizationAccessAsync(userId, organizationId, cancellationToken);
            RequireRole(access, Roles.Owner);
            if (require && !(await mfa.StatusAsync(Instance, userId, cancellationToken)).Enabled
                && !await passkeys.EnrolledAsync(Instance, userId, cancellationToken))
            {
                throw Conflict("mfa_not_enrolled", "Turn on two-factor authentication for your own account first.");
            }

            var changed = access.Organization.Value with { RequireMfa = require };
            return new Plan<OrganizationRecord>(
            [
                Authority(access),
                Change.Update(access.Organization, changed),
                this.Audit(organizationId, userId, require ? "organization.mfa_required" : "organization.mfa_optional"),
            ], changed);
        }, cancellationToken);

    public async Task AuditFactorAsync(string userId, string action, CancellationToken cancellationToken)
    {
        try
        {
            var organizations = (await data.ReadAsync(connection => connection.QueryAsync<string>(
                "SELECT organization_id FROM memberships WHERE user_id = @userId", new { userId }), cancellationToken)).AsList();
            if (organizations.Count > 0)
            {
                await data.SaveAsync([.. organizations.Select(organizationId => this.Audit(organizationId, userId, action, userId))], cancellationToken);
            }
        }
        catch (Exception error) when (error is HttpRequestException or TimeoutException or ApiException)
        {
            logger.ZLogWarning($"Could not record {action:@Action} in a user's organizations ({error.GetType().Name:@Error})");
        }
    }

    private async Task RequireMfaAsync(OrganizationRecord organization, string userId, CancellationToken cancellationToken)
    {
        if (organization.RequireMfa && !(await mfa.StatusAsync(Instance, userId, cancellationToken)).Enabled
            && !await passkeys.EnrolledAsync(Instance, userId, cancellationToken))
        {
            throw new ApiException(StatusCodes.Status403Forbidden, MfaRequiredCode, "This organization requires two-factor authentication. Turn it on at /ui/terrascale/security.");
        }
    }

    public Task<OrganizationRecord> RenameOrganizationAsync(string userId, string organizationId, string name, CancellationToken cancellationToken) =>
        this.CommitAsync(async () =>
        {
            var access = await this.OrganizationAccessAsync(userId, organizationId, cancellationToken);
            RequireRole(access, Roles.Admin);
            var renamed = access.Organization.Value with { Name = name };
            return new Plan<OrganizationRecord>(
            [
                Authority(access),
                Change.Update(access.Organization, renamed),
                this.Audit(organizationId, userId, "organization.renamed", null, name),
            ], renamed);
        }, cancellationToken);

    public Task DeleteOrganizationAsync(string userId, string organizationId, CancellationToken cancellationToken) =>
        this.DeleteOrganizationAsync(userId, organizationId, closingAccount: false, cancellationToken);

    internal Task DeleteOrganizationAsync(string userId, string organizationId, bool closingAccount, CancellationToken cancellationToken) =>
        this.CommitAsync(async () =>
        {
            var access = await this.OrganizationAccessAsync(userId, organizationId, requireMfa: !closingAccount, cancellationToken);
            RequireRole(access, Roles.Owner);
            if (access.Organization.Value.Projects > 0)
            {
                throw Conflict("not_empty", "Delete the organization's projects first.");
            }

            return new Plan<bool>(
            [
                Authority(access),
                Change.Delete(access.Organization),
                DeletionJob.Enqueue(Instance, "organization", organizationId, clock),
            ], true);
        }, cancellationToken);

    public async Task<Page<(MembershipRecord Member, string Email)>> MembersAsync(string userId, string organizationId, int limit, string? after, CancellationToken cancellationToken)
    {
        var access = await this.OrganizationAccessAsync(userId, organizationId, cancellationToken);
        RequireRole(access, Roles.Viewer);
        var from = Cursor(after);
        var take = limit + 1;
        var members = (await data.ReadAsync(connection => connection.QueryAsync<MembershipRecord>(
            "SELECT * FROM memberships WHERE organization_id = @organizationId AND role IS NOT NULL AND user_id > @from ORDER BY user_id LIMIT @take",
            new { organizationId, from, take }), cancellationToken)).AsList();
        return Paged(await this.WithEmailsAsync(members, member => member.UserId, cancellationToken), limit, row => row.Item.UserId);
    }

    public async Task<(MembershipRecord Member, string Email)> ChangeRoleAsync(string userId, string organizationId, string memberId, string role, CancellationToken cancellationToken)
    {
        var changed = await this.CommitAsync(async () =>
        {
            var access = await this.OrganizationAccessAsync(userId, organizationId, cancellationToken);
            RequireRole(access, Roles.Admin);
            var member = memberId == userId ? access.Membership : await this.MembershipAsync(organizationId, memberId, cancellationToken);
            if (member?.Value.Role is not { } current)
            {
                throw NotFound();
            }

            var owners = current == Roles.Owner || role == Roles.Owner;
            if (owners)
            {
                RequireRole(access, Roles.Owner);
            }

            if (current == role)
            {
                return new Plan<MembershipRecord>([], member.Value);
            }

            var organization = access.Organization.Value;
            var count = organization.Owners + (role == Roles.Owner ? 1 : 0) - (current == Roles.Owner ? 1 : 0);
            if (count < 1)
            {
                throw Conflict("last_owner", "An organization keeps at least one owner.");
            }

            if (Roles.Rank(role) > Roles.Rank(current))
            {
                await this.FenceAsync(organizationId, null, memberId, cancellationToken);
            }

            var updated = member.Value with { Role = role };
            var reshaped = Unoffered(organization with { Owners = count }, memberId, role);
            return new Plan<MembershipRecord>(
            [
                Authority(access),
                Change.Update(member, updated),
                reshaped != organization ? Change.Update(access.Organization, reshaped) : Change.Assert(access.Organization),
                this.Audit(organizationId, userId, "member.role_changed", memberId, role),
            ], updated);
        }, cancellationToken);
        return (await this.WithEmailsAsync([changed], member => member.UserId, cancellationToken)).Single();
    }

    public Task RemoveAsync(string userId, string organizationId, string memberId, CancellationToken cancellationToken) =>
        this.CommitAsync(async () =>
        {
            var leaving = userId == memberId;
            var access = await this.OrganizationAccessAsync(userId, organizationId, requireMfa: !leaving, cancellationToken);
            if (!leaving)
            {
                RequireRole(access, Roles.Admin);
            }

            var member = leaving ? access.Membership : await this.MembershipAsync(organizationId, memberId, cancellationToken) ?? throw NotFound();
            var owner = member.Value.Role == Roles.Owner;
            if (owner && !leaving)
            {
                RequireRole(access, Roles.Owner);
            }

            if (owner && access.Organization.Value.Owners <= 1)
            {
                throw Conflict("last_owner", "An organization keeps at least one owner.");
            }

            var changes = new List<Change>();
            if (!leaving)
            {
                changes.Add(Authority(access));
            }

            changes.Add(Change.Delete(member));
            var reshaped = Unoffered(access.Organization.Value with { Owners = access.Organization.Value.Owners - (owner ? 1 : 0) }, memberId, null);
            if (reshaped != access.Organization.Value)
            {
                changes.Add(Change.Update(access.Organization, reshaped));
            }

            changes.AddRange((await this.GrantsAsync(memberId, organizationId, cancellationToken)).Select(grant => Change.Delete(grant)));
            changes.Add(this.Audit(organizationId, userId, leaving ? "member.left" : "member.removed", memberId));
            return new Plan<bool>(changes, true);
        }, cancellationToken);

    internal async Task<(IReadOnlyList<string> Sole, IReadOnlyList<string> Shared)> AccountScopeAsync(string userId, CancellationToken cancellationToken)
    {
        var (sole, shared) = (new List<string>(), new List<string>());
        var memberships = await data.ReadAsync(connection => connection.QueryAsync<MembershipScope>(
            """
            SELECT m.organization_id, m.role,
                m.role IS NOT NULL AND EXISTS (SELECT 1 FROM memberships o WHERE o.organization_id = m.organization_id AND o.user_id <> @userId AND o.role IS NOT NULL) AS others,
                (SELECT owners FROM organizations g WHERE g.id = m.organization_id) AS owners
            FROM memberships m WHERE m.user_id = @userId
            """, new { userId }), cancellationToken);
        foreach (var row in memberships)
        {
            if (row.Role == Roles.Owner && !row.Others)
            {
                sole.Add(row.OrganizationId);
                continue;
            }

            if (row.Role == Roles.Owner && row.Owners is <= 1)
            {
                throw Conflict("last_owner", "Make another member an owner of every organization you share before deleting your account.");
            }

            shared.Add(row.OrganizationId);
        }

        if (sole.Contains(PlatformBootstrap.OrganizationId))
        {
            throw Conflict("platform_project_protected", "TerraScale's own organization holds its services' databases and cannot be deleted; give it another owner before deleting your account.");
        }

        return (sole, shared);
    }

    internal async Task<List<string>> ProjectIdsAsync(string organizationId, CancellationToken cancellationToken) =>
        (await data.ReadAsync(connection => connection.QueryAsync<string>("SELECT id FROM projects WHERE organization_id = @organizationId ORDER BY id", new { organizationId }),
            cancellationToken)).AsList();

    internal async Task<Change?> OwnerAssertionAsync(string userId, string organizationId, CancellationToken cancellationToken) =>
        await this.MembershipAsync(organizationId, userId, cancellationToken) is { Value.Role: Roles.Owner } membership
            ? Change.Assert(membership)
            : null;

    public Task<ProjectRecord> CreateProjectAsync(string userId, string organizationId, string name, string minimumRole, CancellationToken cancellationToken) =>
        this.CommitAsync(async () =>
        {
            var access = await this.OrganizationAccessAsync(userId, organizationId, cancellationToken);
            RequireRole(access, minimumRole);
            await this.FenceAsync(organizationId, null, null, cancellationToken);
            var now = clock.GetUtcNow();
            var project = new ProjectRecord(AuthIds.New(), Instance, organizationId, name, userId, now);
            var grant = new ProjectMemberRecord(Instance, organizationId, project.Id, userId, Roles.Admin, now);
            var organization = access.Organization.Value;
            return new Plan<ProjectRecord>(
            [
                Change.Update(access.Membership, access.Membership.Value),
                Change.Update(access.Organization, organization with { Projects = organization.Projects + 1 }),
                Change.Insert(project),
                Change.Insert(grant),
                this.Audit(organizationId, userId, "project.created", project.Id, name),
            ], project, token => this.NameTakenAsync(organizationId, null, name, token));
        }, cancellationToken);

    public async Task<Page<(ProjectRecord Project, string Role)>> ProjectsAsync(string userId, string organizationId, int limit, string? after, CancellationToken cancellationToken)
    {
        var access = await this.OrganizationAccessAsync(userId, organizationId, cancellationToken);
        var from = Cursor(after);
        if (access.Role is { } role)
        {
            var take = limit + 1;
            var projects = (await data.ReadAsync(connection => connection.QueryAsync<ProjectRecord>(
                "SELECT * FROM projects WHERE organization_id = @organizationId AND id > @from ORDER BY id LIMIT @take", new { organizationId, from, take }), cancellationToken)).AsList();
            var ids = projects.Select(project => project.Id).ToList();
            var held = (await data.ReadAsync(connection => connection.QueryAsync<ProjectMemberRecord>(
                "SELECT * FROM project_members WHERE user_id = @userId AND project_id IN @ids", new { userId, ids }), cancellationToken))
                .ToDictionary(grant => grant.ProjectId, grant => grant.Role, StringComparer.Ordinal);
            return Paged([.. projects.Select(project => (project, RoleOf(Roles.Higher(Roles.Implied(role), held.GetValueOrDefault(project.Id)))))], limit, row => row.project.Id);
        }

        var grantTake = limit + 1;
        var grants = (await data.ReadAsync(connection => connection.QueryAsync<ProjectMemberRecord>(
            "SELECT * FROM project_members WHERE user_id = @userId AND organization_id = @organizationId AND project_id > @from ORDER BY project_id LIMIT @grantTake",
            new { userId, organizationId, from, grantTake }), cancellationToken)).AsList();
        var projectIds = grants.Select(grant => grant.ProjectId).ToList();
        var granted = (await data.ReadAsync(connection => connection.QueryAsync<ProjectRecord>("SELECT * FROM projects WHERE id IN @projectIds", new { projectIds }), cancellationToken))
            .ToDictionary(project => project.Id, StringComparer.Ordinal);
        return Paged([.. grants.Where(grant => granted.ContainsKey(grant.ProjectId)).Select(grant => (granted[grant.ProjectId], grant.Role))], limit, row => row.Item1.Id);
    }

    public Task<ProjectAccess> ProjectAccessAsync(string userId, string organizationId, string projectId, CancellationToken cancellationToken) =>
        this.ProjectAccessAsync(userId, organizationId, projectId, requireMfa: true, cancellationToken);

    private async Task<ProjectAccess> ProjectAccessAsync(string userId, string organizationId, string projectId, bool requireMfa, CancellationToken cancellationToken)
    {
        if (!IsId(organizationId) || !IsId(projectId))
        {
            throw NotFound();
        }

        var project = await this.ProjectAsync(projectId, cancellationToken);
        var organization = project?.Value.OrganizationId == organizationId ? await this.OrganizationAsync(organizationId, cancellationToken) : null;
        if (project is null || organization is null)
        {
            throw NotFound();
        }

        var member = await this.MembershipAsync(organizationId, userId, cancellationToken);
        var granted = await this.ProjectMemberAsync(projectId, userId, cancellationToken);
        if (member?.Value.Role is null && granted is null)
        {
            throw NotFound();
        }

        if (requireMfa)
        {
            await this.RequireMfaAsync(organization.Value, userId, cancellationToken);
        }

        return new(member, organization, project, granted);
    }

    public Task<ProjectAccess> RenameProjectAsync(string userId, string organizationId, string projectId, string name, CancellationToken cancellationToken) =>
        this.CommitAsync(async () =>
        {
            var access = await this.ProjectAccessAsync(userId, organizationId, projectId, cancellationToken);
            RequireRole(access, Roles.Admin);
            await this.FenceAsync(organizationId, projectId, null, cancellationToken);
            var renamed = access.Project.Value with { Name = name };
            return new Plan<ProjectAccess>(
            [
                Authority(access, Roles.Admin),
                Change.Update(access.Project, renamed),
                this.Audit(organizationId, userId, "project.renamed", projectId, name),
            ], access with { Project = access.Project with { Value = renamed } }, token => this.NameTakenAsync(organizationId, projectId, name, token));
        }, cancellationToken);

    public Task<ProjectAccess> SetProjectAllowedCidrsAsync(string userId, string organizationId, string projectId, IReadOnlyList<string> allowedCidrs, CancellationToken cancellationToken) =>
        this.CommitAsync(async () =>
        {
            var access = await this.ProjectAccessAsync(userId, organizationId, projectId, cancellationToken);
            RequireRole(access, Roles.Admin);
            await this.FenceAsync(organizationId, projectId, null, cancellationToken);
            var changed = access.Project.Value with { AllowedCidrs = allowedCidrs };
            return new Plan<ProjectAccess>(
            [
                Authority(access, Roles.Admin),
                Change.Update(access.Project, changed),
                this.Audit(organizationId, userId, "project.allowed_cidrs_changed", projectId, string.Join(' ', allowedCidrs)),
            ], access with { Project = access.Project with { Value = changed } });
        }, cancellationToken);

    internal async Task<Versioned<ClosureJob>> RequestProjectClosureAsync(string userId, string organizationId, string projectId, string? accountId, CancellationToken cancellationToken)
    {
        await this.CommitAsync(async () =>
        {
            var access = await this.ProjectAccessAsync(userId, organizationId, projectId, requireMfa: accountId is null, cancellationToken);
            RequireRole(access, Roles.Admin);
            if (projectId is PlatformBootstrap.AuthProjectId or PlatformBootstrap.DashboardProjectId)
            {
                throw PlatformProjectProtected();
            }

            var account = accountId is null ? null : await this.ClosureAsync("account", accountId, cancellationToken);
            if (accountId is not null && account is null)
            {
                throw ApiException.Unavailable();
            }

            if (await this.ClosureAsync("project", projectId, cancellationToken) is { } existing)
            {
                if (account is not null && existing.Value.ProjectClosureVersion == 1)
                {
                    var adopted = existing.Value with { AccountId = accountId, ProjectClosureVersion = 2, Generation = AuthIds.New(), NextAttemptAt = clock.GetUtcNow() };
                    return new Plan<bool>([Authority(access, Roles.Admin), Change.Assert(account), Change.Update(existing, adopted)], true);
                }

                return new Plan<bool>([], true);
            }

            var project = access.Project.Value;
            var closureId = project.ClosureId ?? AuthIds.New();
            var (_, create) = ClosureJob.New(Instance, "project", projectId, organizationId, userId, clock, accountId, closureId);
            List<Change> changes =
            [
                Authority(access, Roles.Admin),
                Change.Update(access.Project, project with { ClosureId = closureId }),
                create,
                this.Audit(organizationId, userId, "project.closure_requested", projectId, project.Name),
            ];
            if (account is not null)
            {
                changes.Add(Change.Assert(account));
            }

            return new Plan<bool>(changes, true);
        }, cancellationToken);
        return await this.ClosureAsync("project", projectId, cancellationToken)
            is { } job && job.Value.OrganizationId == organizationId ? job : throw ApiException.Unavailable();
    }

    internal async Task<Versioned<ClosureJob>?> ClosureAsync(string scope, string subjectId, CancellationToken cancellationToken)
    {
        var row = Versioned.Of(await data.ReadAsync(connection => connection.QuerySingleOrDefaultAsync<ClosureJob>(
            "SELECT * FROM closure_jobs WHERE instance_id = @Instance AND scope = @scope AND subject_id = @subjectId", new { Instance, scope, subjectId }), cancellationToken));
        if (row is null || !IsId(row.Value.Id))
        {
            return null;
        }

        var generation = row.Value.Generation;
        var committed = scope == "account" && row.Value.CommittedAt is null && generation is not null
            ? await data.ReadAsync(connection => connection.QuerySingleOrDefaultAsync<ClosureCommit>(
                "SELECT * FROM closure_commits WHERE generation = @generation", new { generation }), cancellationToken)
            : null;
        return committed is { } commit ? row with { Value = row.Value with { CommittedAt = commit.CommittedAt } } : row;
    }

    internal async Task<bool> CancelClosureAsync(Versioned<ClosureJob> job, CancellationToken cancellationToken, string? unlessAccount = null)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (job.Value.Scope == "account")
        {
            return await this.CancelAccountClosureAsync(job.Value, cancellationToken) switch
            {
                "cancelled" => true,
                "committed" => false,
                _ => throw ApiException.Unavailable(),
            };
        }

        if (unlessAccount is not null && await this.ClosureAsync("account", unlessAccount, cancellationToken) is not null)
        {
            return false;
        }

        return await data.SaveAsync(Change.Delete(job), cancellationToken);
    }

    internal async Task<string> CancelAccountClosureAsync(ClosureJob job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        var generation = job.Generation ?? throw new InvalidOperationException("An account closure needs a generation.");
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var state = await data.ReadAsync(connection => connection.QuerySingleAsync<ClosureState>(
                """
                SELECT EXISTS (SELECT 1 FROM closure_commits WHERE generation = @generation) AS committed,
                    EXISTS (SELECT 1 FROM closure_cancellations WHERE generation = @generation) AS cancelling
                """, new { generation }), cancellationToken);
            if (state.Committed)
            {
                return "committed";
            }

            var current = await this.ClosureAsync("account", job.SubjectId, cancellationToken);
            var live = current is { } stored && stored.Value.Generation == generation ? stored : null;
            if (live?.Value.CommittedAt is not null)
            {
                return "committed";
            }

            List<Change> changes = [];
            if (!state.Cancelling)
            {
                changes.Add(Change.Insert(new ClosureCancellation(generation)));
            }

            if (live is not null)
            {
                changes.Add(Change.Delete(live));
            }

            if (await data.SaveAsync(changes, cancellationToken))
            {
                return "cancelled";
            }
        }

        throw ApiException.Unavailable();
    }

    internal Task<bool> IsClosureCancellingAsync(ClosureJob job, CancellationToken cancellationToken) =>
        job.Generation is { } generation
            ? data.ReadAsync(connection => connection.ExecuteScalarAsync<bool>("SELECT EXISTS (SELECT 1 FROM closure_cancellations WHERE generation = @generation)", new { generation }),
                cancellationToken)
            : Task.FromResult(false);

    internal async Task<string?> CommitAccountClosureAsync(Versioned<ClosureJob> job, ClosureJob committed, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(committed);
        if (committed.Scope != "account" || committed.Generation is not { } generation || committed.CommittedAt is not { } at)
        {
            throw new ArgumentException("A committed account generation is required.", nameof(committed));
        }

        if (await this.IsClosureCancellingAsync(committed, cancellationToken))
        {
            return null;
        }

        var update = Change.Update(job, committed);
        return await data.SaveAsync([update, Change.Insert(new ClosureCommit(generation, at))], cancellationToken) ? update.Version : null;
    }

    internal async Task<bool> RemoveClosedProjectAsync(Versioned<ClosureJob> job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        var (projectId, organizationId) = (job.Value.SubjectId, job.Value.OrganizationId!);
        var removed = await this.CommitAsync(async () =>
        {
            if (await this.ClosureAsync("project", projectId, cancellationToken) is not { } current || !current.Value.Same(job.Value))
            {
                return new Plan<bool>([], await this.ProjectAsync(projectId, cancellationToken) is null);
            }

            List<Change> changes = [Change.Delete(current)];
            if (await this.ProjectAsync(projectId, cancellationToken) is { } project && project.Value.OrganizationId == organizationId
                && await this.OrganizationAsync(organizationId, cancellationToken) is { } organization)
            {
                changes.Add(Change.Update(organization, organization.Value with { Projects = organization.Value.Projects - 1 }));
                changes.Add(Change.Delete(project));
                changes.Add(DeletionJob.Enqueue(Instance, "project", projectId, clock, organizationId));
                if (await users.GetAsync(Instance, job.Value.RequestedBy, cancellationToken) is not null)
                {
                    changes.Add(this.Audit(organizationId, job.Value.RequestedBy, "project.deleted", projectId, project.Value.Name));
                }
            }

            return new Plan<bool>(changes, true);
        }, cancellationToken);
        if (!removed)
        {
            return false;
        }

        await this.RetireCollaboratorsAsync(organizationId, cancellationToken);
        return true;
    }

    internal async Task RetireCollaboratorsAsync(string organizationId, CancellationToken cancellationToken)
    {
        try
        {
            var orphans = Versioned.All(await data.ReadAsync(connection => connection.QueryAsync<MembershipRecord>(
                """
                SELECT * FROM memberships m WHERE m.organization_id = @organizationId AND m.role IS NULL
                    AND NOT EXISTS (SELECT 1 FROM project_members g WHERE g.organization_id = @organizationId AND g.user_id = m.user_id)
                """, new { organizationId }), cancellationToken));
            foreach (var orphan in orphans)
            {
                await data.SaveAsync(Change.Delete(orphan), cancellationToken);
            }
        }
        catch (Exception error) when (error is HttpRequestException or TimeoutException or ApiException)
        {
            logger.ZLogWarning($"Cleaning up collaborators after a project deletion failed ({error.GetType().Name:@Error}); what is left grants nothing");
        }
    }

    public async Task<Page<(ProjectMemberRecord Member, string Email)>> ProjectMembersAsync(string userId, string organizationId, string projectId, int limit, string? after, CancellationToken cancellationToken)
    {
        _ = await this.ProjectAccessAsync(userId, organizationId, projectId, cancellationToken);
        var from = Cursor(after);
        var take = limit + 1;
        var grants = (await data.ReadAsync(connection => connection.QueryAsync<ProjectMemberRecord>(
            "SELECT * FROM project_members WHERE project_id = @projectId AND user_id > @from ORDER BY user_id LIMIT @take", new { projectId, from, take }), cancellationToken)).AsList();
        return Paged(await this.WithEmailsAsync(grants, grant => grant.UserId, cancellationToken), limit, row => row.Item.UserId);
    }

    public async Task<(ProjectMemberRecord Member, string Email)> SetProjectRoleAsync(string userId, string organizationId, string projectId, string memberId, string role, CancellationToken cancellationToken)
    {
        var granted = await this.CommitAsync(async () =>
        {
            var access = await this.ProjectAccessAsync(userId, organizationId, projectId, cancellationToken);
            RequireRole(access, Roles.Admin);
            var self = memberId == userId;
            var member = self ? access.Membership : await this.MembershipAsync(organizationId, memberId, cancellationToken);
            var existing = self ? access.Grant : await this.ProjectMemberAsync(projectId, memberId, cancellationToken);
            if (member is null || existing is null && member.Value.Role is null)
            {
                throw NotFound();
            }

            var lowers = existing is not null && Roles.Rank(role) < Roles.Rank(existing.Value.Role);
            if (!lowers)
            {
                await this.FenceAsync(organizationId, projectId, memberId, cancellationToken);
            }

            var grant = existing is null ? new ProjectMemberRecord(Instance, organizationId, projectId, memberId, role, clock.GetUtcNow()) : existing.Value with { Role = role };
            return new Plan<ProjectMemberRecord>(
            [
                Authority(access, Roles.Admin),
                Change.Update(member, member.Value),
                Change.Assert(access.Project),
                existing is null ? Change.Insert(grant) : Change.Update(existing, grant),
                this.Audit(organizationId, userId, "project_member.role_set", memberId, $"{projectId}/{role}"),
            ], grant);
        }, cancellationToken);
        return (await this.WithEmailsAsync([granted], grant => grant.UserId, cancellationToken)).Single();
    }

    public Task RemoveProjectRoleAsync(string userId, string organizationId, string projectId, string memberId, CancellationToken cancellationToken) =>
        this.CommitAsync(async () =>
        {
            var access = await this.ProjectAccessAsync(userId, organizationId, projectId, cancellationToken);
            RequireRole(access, Roles.Admin);
            var self = memberId == userId;
            var existing = (self ? access.Grant : await this.ProjectMemberAsync(projectId, memberId, cancellationToken)) ?? throw NotFound();
            var member = self ? access.Membership : await this.MembershipAsync(organizationId, memberId, cancellationToken);
            List<Change> changes = [Authority(access, Roles.Admin), Change.Delete(existing)];
            if (member is not null)
            {
                changes.Add(member.Value.Role is null && !await this.CollaboratesAsync(memberId, organizationId, projectId, cancellationToken)
                    ? Change.Delete(member)
                    : Change.Update(member, member.Value));
            }

            changes.Add(this.Audit(organizationId, userId, "project_member.removed", memberId, projectId));
            return new Plan<bool>(changes, true);
        }, cancellationToken);

    public async Task<bool> AddMemberAsync(string organizationId, string userId, string role, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            if (await this.JoinAsync(organizationId, userId, role, cancellationToken) is not { } changes)
            {
                return false;
            }

            if (await data.SaveAsync(changes, cancellationToken))
            {
                return true;
            }
        }

        throw ApiException.Unavailable();
    }

    public async Task<bool> GrantAsync(string projectId, string userId, string role, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            if (await this.GrantOperationsAsync(projectId, userId, role, refuseHeld: false, cancellationToken) is not { } changes)
            {
                return false;
            }

            if (await data.SaveAsync(changes, cancellationToken))
            {
                return true;
            }
        }

        throw ApiException.Unavailable();
    }

    internal async Task<List<Change>?> JoinAsync(string organizationId, string userId, string role, CancellationToken cancellationToken)
    {
        if (await this.OrganizationAsync(organizationId, cancellationToken) is not { } organization
            || await this.MembershipAsync(organizationId, userId, cancellationToken) is var existing && existing?.Value.Role is not null
            || await users.GetAsync(Instance, userId, cancellationToken) is not { } user)
        {
            return null;
        }

        await this.FenceAsync(organizationId, null, userId, cancellationToken);
        var membership = new MembershipRecord(Instance, organizationId, userId, role, existing?.Value.JoinedAt ?? clock.GetUtcNow());
        return
        [
            Change.Assert(user),
            existing is null ? Change.Insert(membership) : Change.Update(existing, membership),
            role == Roles.Owner ? Change.Update(organization, organization.Value with { Owners = organization.Value.Owners + 1 }) : Change.Assert(organization),
        ];
    }

    internal async Task<List<Change>?> GrantOperationsAsync(string projectId, string userId, string role, bool refuseHeld, CancellationToken cancellationToken)
    {
        if (await this.ProjectAsync(projectId, cancellationToken) is not { } project
            || await users.GetAsync(Instance, userId, cancellationToken) is not { } user)
        {
            return null;
        }

        var organizationId = project.Value.OrganizationId;
        var member = await this.MembershipAsync(organizationId, userId, cancellationToken);
        var existing = await this.ProjectMemberAsync(projectId, userId, cancellationToken);
        if (refuseHeld && Roles.Rank(Roles.Higher(Roles.Implied(member?.Value.Role), existing?.Value.Role)) >= Roles.Rank(role))
        {
            throw Conflict("already_member", "You already have this access to the project.");
        }

        await this.FenceAsync(organizationId, projectId, userId, cancellationToken);
        var now = clock.GetUtcNow();
        var grant = existing is null ? new ProjectMemberRecord(Instance, organizationId, projectId, userId, role, now) : existing.Value with { Role = Roles.Higher(existing.Value.Role, role)! };
        return
        [
            Change.Assert(user),
            member is null ? Change.Insert(new MembershipRecord(Instance, organizationId, userId, null, now)) : Change.Update(member, member.Value),
            Change.Assert(project),
            existing is null ? Change.Insert(grant) : Change.Update(existing, grant),
        ];
    }

    public async Task<OwnershipTransferRecord> TransferAsync(string userId, string organizationId, CancellationToken cancellationToken)
    {
        var access = await this.OrganizationAccessAsync(userId, organizationId, cancellationToken);
        RequireRole(access, Roles.Viewer);
        return (await this.LiveTransferAsync(access.Organization, cancellationToken) ?? throw NotFound()).Transfer;
    }

    public Task<OwnershipTransferRecord> StartTransferAsync(string userId, string organizationId, string nomineeId, CancellationToken cancellationToken) =>
        this.CommitAsync(async () =>
        {
            var access = await this.OrganizationAccessAsync(userId, organizationId, cancellationToken);
            RequireRole(access, Roles.Owner);
            var nominee = await this.MembershipAsync(organizationId, nomineeId, cancellationToken);
            if (nominee?.Value.Role is not { } role)
            {
                throw NotFound();
            }

            if (role == Roles.Owner)
            {
                throw new ApiException(StatusCodes.Status400BadRequest, "invalid_request", "The nominee is already an owner.");
            }

            var now = clock.GetUtcNow();
            var transfer = new OwnershipTransferRecord(userId, nomineeId, now, now + TransferLifetime);
            return new Plan<OwnershipTransferRecord>(
            [
                Authority(access),
                Change.Assert(nominee),
                Change.Update(access.Organization, access.Organization.Value with { Transfer = transfer }),
                this.Audit(organizationId, userId, "ownership.transfer_started", nomineeId),
            ], transfer);
        }, cancellationToken);

    public Task CancelTransferAsync(string userId, string organizationId, CancellationToken cancellationToken) =>
        this.CommitAsync(async () =>
        {
            var access = await this.OrganizationAccessAsync(userId, organizationId, cancellationToken);
            RequireRole(access, Roles.Viewer);
            var live = await this.LiveTransferAsync(access.Organization, cancellationToken) ?? throw NotFound();
            var nominee = live.Transfer.ToUserId == userId;
            if (!nominee)
            {
                RequireRole(access, Roles.Owner);
            }

            return new Plan<bool>(
            [
                Authority(access),
                Change.Update(access.Organization, access.Organization.Value with { Transfer = null }),
                this.Audit(organizationId, userId, "ownership.transfer_cancelled", live.Transfer.ToUserId),
            ], true);
        }, cancellationToken);

    public async Task<(MembershipRecord Member, string Email)> ConfirmTransferAsync(string userId, string organizationId, CancellationToken cancellationToken)
    {
        var confirmed = await this.CommitAsync(async () =>
        {
            var access = await this.OrganizationAccessAsync(userId, organizationId, cancellationToken);
            var live = await this.LiveTransferAsync(access.Organization, cancellationToken) ?? throw NotFound();
            if (live.Transfer.ToUserId != userId)
            {
                throw Forbidden();
            }

            await this.FenceAsync(organizationId, null, userId, cancellationToken);
            var owner = access.Membership.Value with { Role = Roles.Owner };
            return new Plan<MembershipRecord>(
            [
                Change.Update(access.Organization, access.Organization.Value with { Transfer = null }),
                Change.Update(access.Membership, owner),
                Change.Update(live.Starter, live.Starter.Value with { Role = Roles.Admin }),
                this.Audit(organizationId, userId, "ownership.transfer_confirmed", userId),
            ], owner);
        }, cancellationToken);
        return (await this.WithEmailsAsync([confirmed], member => member.UserId, cancellationToken)).Single();
    }

    public async Task<Page<AuditEventRecord>> AuditEventsAsync(string userId, string organizationId, int limit, string? after, CancellationToken cancellationToken)
    {
        var access = await this.OrganizationAccessAsync(userId, organizationId, cancellationToken);
        RequireRole(access, Roles.Admin);
        var before = after is null ? null : Cursor(after);
        var take = limit + 1;
        var rows = (await data.ReadAsync(connection => connection.QueryAsync<AuditEventRecord>(
            "SELECT * FROM audit_events WHERE organization_id = @organizationId AND (@before IS NULL OR id < @before) ORDER BY id DESC LIMIT @take",
            new { organizationId, before, take }), cancellationToken)).AsList();
        return Paged(rows, limit, item => item.Id);
    }

    internal Change Audit(string organizationId, string actor, string action, string? target = null, string? detail = null) =>
        Change.Insert(new AuditEventRecord(AuthIds.New(), Instance, organizationId, actor, action, target, detail, clock.GetUtcNow()));

    public static void RequireRole(OrganizationAccess access, string minimum)
    {
        ArgumentNullException.ThrowIfNull(access);
        if (Roles.Rank(access.Role) < Roles.Rank(minimum))
        {
            throw Forbidden();
        }
    }

    public static void RequireRole(ProjectAccess access, string minimum)
    {
        ArgumentNullException.ThrowIfNull(access);
        if (Roles.Rank(access.Role) < Roles.Rank(minimum))
        {
            throw Forbidden();
        }
    }

    internal static ApiException NotFound() => new(StatusCodes.Status404NotFound, "not_found", "No such resource.");

    internal static ApiException Forbidden() => new(StatusCodes.Status403Forbidden, "forbidden_role", "Your role does not allow this.");

    internal static ApiException Conflict(string code, string message) => new(StatusCodes.Status409Conflict, code, message);

    internal static ApiException PlatformProjectProtected() =>
        Conflict("platform_project_protected", "This project holds a TerraScale service's own database and cannot be deleted.");

    internal static AuthNotFound NotFoundFailure() => new("No such resource.", "not_found");

    internal static Change Authority(OrganizationAccess access)
    {
        ArgumentNullException.ThrowIfNull(access);
        return Change.Assert(access.Membership);
    }

    internal static Change Authority(ProjectAccess access, string minimum)
    {
        ArgumentNullException.ThrowIfNull(access);
        return Roles.Rank(Roles.Implied(access.Membership?.Value.Role)) >= Roles.Rank(minimum)
            ? Change.Assert(access.Membership!)
            : Change.Assert(access.Grant!);
    }

    internal static string Cursor(string? after) =>
        after is null ? string.Empty
        : IsId(after) ? after
        : throw new ApiException(StatusCodes.Status400BadRequest, "invalid_request", "after is not a cursor of this list.");

    internal static Page<T> Paged<T>(IReadOnlyList<T> rows, int limit, Func<T, string> key) =>
        rows.Count > limit ? new([.. rows.Take(limit)], key(rows[limit - 1])) : new(rows, null);

    internal async Task<T> CommitAsync<T>(Func<Task<Plan<T>>> plan, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            var next = await plan();
            if (next.Operations.Count == 0 || await data.SaveAsync(next.Operations, cancellationToken))
            {
                return next.Result;
            }

            if (next.Taken is not null && await next.Taken(cancellationToken))
            {
                throw next.TakenError ?? Conflict("name_taken", "The organization already has a project with this name.");
            }
        }

        throw ApiException.Unavailable();
    }

    private async Task RefuseOrganizationCreationAsync(string organizationId, CancellationToken cancellationToken)
    {
        if (await data.ReadAsync(connection => connection.ExecuteScalarAsync<bool>(
            """
            SELECT EXISTS (SELECT 1 FROM deletion_jobs WHERE instance_id = @Instance AND scope = 'organization' AND subject_id = @organizationId)
                OR EXISTS (SELECT 1 FROM closure_jobs WHERE instance_id = @Instance AND scope = 'organization' AND subject_id = @organizationId)
            """, new { Instance, organizationId }), cancellationToken))
        {
            throw Conflict("organization_deleted", "This organization was deleted or is closing. Start a new organization checkout.");
        }
    }

    internal async Task FenceAsync(string? organizationId, string? projectId, string? userId, CancellationToken cancellationToken)
    {
        if (await data.ReadAsync(connection => connection.ExecuteScalarAsync<bool>(
            """
            SELECT EXISTS (SELECT 1 FROM closure_jobs WHERE instance_id = @Instance AND (scope = 'organization' AND subject_id = @organizationId
                OR scope = 'project' AND subject_id = @projectId OR scope = 'account' AND subject_id = @userId))
            """, new { Instance, organizationId, projectId, userId }), cancellationToken))
        {
            throw ApiException.Unavailable();
        }
    }

    internal async Task<Versioned<OrganizationRecord>?> OrganizationAsync(string organizationId, CancellationToken cancellationToken) =>
        Versioned.Of(await data.ReadAsync(connection => connection.QuerySingleOrDefaultAsync<OrganizationRecord>(
            "SELECT * FROM organizations WHERE id = @organizationId AND instance_id = @Instance", new { organizationId, Instance }), cancellationToken));

    internal async Task<string?> RoleAsync(string organizationId, string userId, CancellationToken cancellationToken) =>
        (await this.MembershipAsync(organizationId, userId, cancellationToken))?.Value.Role;

    internal async Task<Versioned<ProjectRecord>?> ProjectAsync(string projectId, CancellationToken cancellationToken) =>
        Versioned.Of(await data.ReadAsync(connection => connection.QuerySingleOrDefaultAsync<ProjectRecord>(
            "SELECT * FROM projects WHERE id = @projectId AND instance_id = @Instance", new { projectId, Instance }), cancellationToken));

    private Task<bool> NameTakenAsync(string organizationId, string? projectId, string name, CancellationToken cancellationToken)
    {
        var key = name.ToUpperInvariant();
        return data.ReadAsync(connection => connection.ExecuteScalarAsync<bool>(
            "SELECT EXISTS (SELECT 1 FROM projects WHERE organization_id = @organizationId AND id IS NOT @projectId AND name_key = @key)", new { organizationId, projectId, key }),
            cancellationToken);
    }

    private static OrganizationRecord Unoffered(OrganizationRecord organization, string userId, string? role) =>
        organization.Transfer is { } transfer
            && (transfer.FromUserId == userId && role != Roles.Owner || transfer.ToUserId == userId && role is null or Roles.Owner)
                ? organization with { Transfer = null }
                : organization;

    private async Task<(OwnershipTransferRecord Transfer, Versioned<MembershipRecord> Starter)?> LiveTransferAsync(Versioned<OrganizationRecord> organization, CancellationToken cancellationToken)
    {
        if (organization.Value.Transfer is not { } transfer || transfer.ExpiresAt <= clock.GetUtcNow()
            || await this.MembershipAsync(organization.Value.Id, transfer.FromUserId, cancellationToken) is not { Value.Role: Roles.Owner } starter
            || await this.MembershipAsync(organization.Value.Id, transfer.ToUserId, cancellationToken) is not { Value.Role: Roles.Viewer or Roles.Member or Roles.Admin })
        {
            return null;
        }

        return (transfer, starter);
    }

    private Task<bool> CollaboratesAsync(string userId, string organizationId, string? except, CancellationToken cancellationToken) =>
        data.ReadAsync(connection => connection.ExecuteScalarAsync<bool>(
            "SELECT EXISTS (SELECT 1 FROM project_members WHERE user_id = @userId AND organization_id = @organizationId AND project_id IS NOT @except)",
            new { userId, organizationId, except }), cancellationToken);

    private async Task<IReadOnlyList<(T Item, string Email)>> WithEmailsAsync<T>(IReadOnlyList<T> items, Func<T, string> userId, CancellationToken cancellationToken)
    {
        var ids = items.Select(userId).Distinct(StringComparer.Ordinal).ToArray();
        var emails = (await data.ReadAsync(connection => connection.QueryAsync<UserEmail>(
            "SELECT id, email FROM users WHERE instance_id = @Instance AND id IN @ids", new { Instance, ids }), cancellationToken))
            .ToDictionary(user => user.Id, user => user.Email, StringComparer.Ordinal);
        return [.. items.Where(item => emails.ContainsKey(userId(item))).Select(item => (item, emails[userId(item)]))];
    }

    private async Task<IReadOnlyList<Versioned<ProjectMemberRecord>>> GrantsAsync(string userId, string organizationId, CancellationToken cancellationToken) =>
        Versioned.All(await data.ReadAsync(connection => connection.QueryAsync<ProjectMemberRecord>(
            "SELECT * FROM project_members WHERE user_id = @userId AND organization_id = @organizationId", new { userId, organizationId }), cancellationToken));

    private async Task<Versioned<MembershipRecord>?> MembershipAsync(string organizationId, string userId, CancellationToken cancellationToken) =>
        Versioned.Of(await data.ReadAsync(connection => connection.QuerySingleOrDefaultAsync<MembershipRecord>(
            "SELECT * FROM memberships WHERE organization_id = @organizationId AND user_id = @userId", new { organizationId, userId }), cancellationToken));

    private async Task<Versioned<ProjectMemberRecord>?> ProjectMemberAsync(string projectId, string userId, CancellationToken cancellationToken) =>
        Versioned.Of(await data.ReadAsync(connection => connection.QuerySingleOrDefaultAsync<ProjectMemberRecord>(
            "SELECT * FROM project_members WHERE project_id = @projectId AND user_id = @userId", new { projectId, userId }), cancellationToken));

    private static string RoleOf(string? role) => role ?? throw new InvalidOperationException("An organization member always holds a project role.");

    internal sealed record UserEmail(string Id, string Email);

    internal sealed record MembershipScope(string OrganizationId, string? Role, bool Others, int? Owners);

    internal sealed record ClosureState(bool Committed, bool Cancelling);

    internal sealed record Plan<T>(IReadOnlyList<Change> Operations, T Result, Func<CancellationToken, Task<bool>>? Taken = null, ApiException? TakenError = null);
}

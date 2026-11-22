using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Errors;
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

public sealed record MemberEmail(MembershipRecord Member, string Email);

public sealed record ProjectMemberEmail(ProjectMemberRecord Member, string Email);

internal sealed record LiveTransfer(OwnershipTransferRecord Transfer, Versioned<MembershipRecord> Starter);

public readonly union OrganizationAccessResult(OrganizationAccess, Failure);

public readonly union ProjectAccessResult(ProjectAccess, Failure);

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
        await this.RefuseOrganizationCreationAsync(id, cancellationToken).ConfigureAwait(false);
        await this.FenceAsync(id, null, user.Id, cancellationToken).ConfigureAwait(false);
        if (idempotencyKey is not null && await this.OrganizationAsync(id, cancellationToken).ConfigureAwait(false) is { } existing)
        {
            return existing.Value.CreatedBy == user.Id ? existing.Value : throw ApiException.Unavailable();
        }

        var now = clock.GetUtcNow();
        var organization = new OrganizationRecord(id, Instance, name, user.Id, now, Owners: 1, Projects: 0);
        var membership = new MembershipRecord(Instance, organization.Id, user.Id, Roles.Owner, now);
        await this.RefuseOrganizationCreationAsync(id, cancellationToken).ConfigureAwait(false);
        await this.FenceAsync(id, null, user.Id, cancellationToken).ConfigureAwait(false);
        if (await data.SaveAsync(
        [
            Change.Insert(organization),
            Change.Insert(membership),
            this.Audit(organization.Id, user.Id, "organization.created", null, name),
        ], cancellationToken).ConfigureAwait(false))
        {
            return organization;
        }

        await this.RefuseOrganizationCreationAsync(id, cancellationToken).ConfigureAwait(false);
        await this.FenceAsync(id, null, user.Id, cancellationToken).ConfigureAwait(false);
        if (idempotencyKey is not null && await this.OrganizationAsync(id, cancellationToken).ConfigureAwait(false) is { } winner && winner.Value.CreatedBy == user.Id)
        {
            return winner.Value;
        }

        throw ApiException.Unavailable();
    }

    public async Task<Page<(OrganizationRecord Organization, string? Role)>> OrganizationsAsync(string userId, int limit, string? after, CancellationToken cancellationToken)
    {
        var from = Cursor(after);
        var rows = await data.ReadAsync((db, token) => db.Memberships
            .Where(membership => membership.UserId == userId && string.Compare(membership.OrganizationId, from) > 0)
            .OrderBy(membership => membership.OrganizationId)
            .Take(limit + 1)
            .Join(db.Organizations, membership => membership.OrganizationId, organization => organization.Id, (membership, organization) => new { membership.Role, Organization = organization })
            .ToListAsync(token), cancellationToken).ConfigureAwait(false);
        var next = rows.Count > limit ? rows[limit - 1].Organization.Id : null;
        var organizations = new List<(OrganizationRecord, string?)>();
        foreach (var row in rows.Take(limit))
        {
            if (row.Role is not null || await this.CollaboratesAsync(userId, row.Organization.Id, null, cancellationToken).ConfigureAwait(false))
            {
                organizations.Add((row.Organization, row.Role));
            }
        }

        return new(organizations, next);
    }

    public Task<OrganizationAccessResult> OrganizationAccessAsync(string userId, string organizationId, CancellationToken cancellationToken) =>
        this.OrganizationAccessAsync(userId, organizationId, requireMfa: true, cancellationToken);

    internal async Task<OrganizationAccess> RequireOrganizationAsync(string userId, string organizationId, CancellationToken cancellationToken) =>
        await this.OrganizationAccessAsync(userId, organizationId, requireMfa: true, cancellationToken).ConfigureAwait(false) switch
        {
            OrganizationAccess access => access,
            Failure failure => throw ToException(failure),
        };

    private async Task<OrganizationAccessResult> OrganizationAccessAsync(string userId, string organizationId, bool requireMfa, CancellationToken cancellationToken)
    {
        if (!IsId(organizationId))
        {
            return NotFoundFailure();
        }

        var row = await data.ReadAsync((db, token) => (
            from organization in db.Organizations.Where(organization => organization.Id == organizationId && organization.InstanceId == Instance)
            from membership in db.Memberships.Where(membership => membership.OrganizationId == organizationId && membership.UserId == userId).DefaultIfEmpty()
            select new
            {
                Organization = organization,
                Version = EF.Property<string>(organization, AuthDbContext.VersionProperty),
                Membership = membership,
                MembershipVersion = EF.Property<string?>(membership, AuthDbContext.VersionProperty),
            }).SingleOrDefaultAsync(token), cancellationToken).ConfigureAwait(false);
        var membership = row?.Membership is { } found ? new Versioned<MembershipRecord>(found, row.MembershipVersion!) : null;
        if (row is null || membership is null
            || membership.Value.Role is null && !await this.CollaboratesAsync(userId, organizationId, null, cancellationToken).ConfigureAwait(false))
        {
            return NotFoundFailure();
        }

        if (requireMfa)
        {
            if (await this.RequireMfaAsync(row.Organization, userId, cancellationToken).ConfigureAwait(false) is { } mfa)
            {
                return mfa;
            }
        }

        return new OrganizationAccess(new(row.Organization, row.Version), membership);
    }

    public Task<PlanOutcome<OrganizationRecord>> SetMfaPolicyAsync(string userId, string organizationId, bool require, CancellationToken cancellationToken) =>
        this.CommitAsync(async () =>
        {
            var read = await this.OrganizationAccessAsync(userId, organizationId, cancellationToken).ConfigureAwait(false);
            if (read is not OrganizationAccess access)
            {
                return Refused<OrganizationRecord>(RefusalOf(read));
            }

            if (RoleRefusal(access, Roles.Owner) is { } forbidden)
            {
                return Refused<OrganizationRecord>(forbidden);
            }

            if (require && !(await mfa.StatusAsync(Instance, userId, cancellationToken).ConfigureAwait(false)).Enabled
                && !await passkeys.EnrolledAsync(Instance, userId, cancellationToken).ConfigureAwait(false))
            {
                return Refused<OrganizationRecord>(new Conflict("Turn on two-factor authentication for your own account first.", "mfa_not_enrolled"));
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
            var organizations = await data.ReadAsync((db, token) => db.Memberships.Where(membership => membership.UserId == userId)
                .Select(membership => membership.OrganizationId).ToListAsync(token), cancellationToken).ConfigureAwait(false);
            if (organizations.Count > 0)
            {
                await data.SaveAsync([.. organizations.Select(organizationId => this.Audit(organizationId, userId, action, userId))], cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception error) when (error is HttpRequestException or TimeoutException or ApiException or DbUpdateException)
        {
            logger.ZLogWarning($"Could not record {action:@Action} in a user's organizations ({error.GetType().Name:@Error})");
        }
    }

    private async Task<Failure?> RequireMfaAsync(OrganizationRecord organization, string userId, CancellationToken cancellationToken)
    {
        if (organization.RequireMfa && !(await mfa.StatusAsync(Instance, userId, cancellationToken).ConfigureAwait(false)).Enabled
            && !await passkeys.EnrolledAsync(Instance, userId, cancellationToken).ConfigureAwait(false))
        {
            return new Forbidden("This organization requires two-factor authentication. Turn it on at /ui/terrascale/security.", MfaRequiredCode);
        }

        return null;
    }

    public Task<PlanOutcome<OrganizationRecord>> RenameOrganizationAsync(string userId, string organizationId, string name, CancellationToken cancellationToken) =>
        this.CommitAsync(async () =>
        {
            var read = await this.OrganizationAccessAsync(userId, organizationId, cancellationToken).ConfigureAwait(false);
            if (read is not OrganizationAccess access)
            {
                return Refused<OrganizationRecord>(RefusalOf(read));
            }

            if (RoleRefusal(access, Roles.Admin) is { } forbidden)
            {
                return Refused<OrganizationRecord>(forbidden);
            }

            var renamed = access.Organization.Value with { Name = name };
            return new Plan<OrganizationRecord>(
            [
                Authority(access),
                Change.Update(access.Organization, renamed),
                this.Audit(organizationId, userId, "organization.renamed", null, name),
            ], renamed);
        }, cancellationToken);

    public Task<PlanOutcome<Done>> DeleteOrganizationAsync(string userId, string organizationId, CancellationToken cancellationToken) =>
        this.DeleteOrganizationAsync(userId, organizationId, closingAccount: false, cancellationToken);

    internal Task<PlanOutcome<Done>> DeleteOrganizationAsync(string userId, string organizationId, bool closingAccount, CancellationToken cancellationToken) =>
        this.CommitAsync(async () =>
        {
            var read = await this.OrganizationAccessAsync(userId, organizationId, requireMfa: !closingAccount, cancellationToken).ConfigureAwait(false);
            if (read is not OrganizationAccess access)
            {
                return Refused<Done>(RefusalOf(read));
            }

            if (RoleRefusal(access, Roles.Owner) is { } forbidden)
            {
                return Refused<Done>(forbidden);
            }

            if (access.Organization.Value.Projects > 0)
            {
                return Refused<Done>(new Conflict("Delete the organization's projects first.", "not_empty"));
            }

            return new Plan<Done>(
            [
                Authority(access),
                Change.Delete(access.Organization),
                DeletionJob.Enqueue(Instance, "organization", organizationId, clock),
            ], new Done());
        }, cancellationToken);

    public async Task<PlanOutcome<Page<(MembershipRecord Member, string Email)>>> MembersAsync(string userId, string organizationId, int limit, string? after, CancellationToken cancellationToken)
    {
        var read = await this.OrganizationAccessAsync(userId, organizationId, cancellationToken).ConfigureAwait(false);
        if (read is not OrganizationAccess access)
        {
            return RefusalOf(read);
        }

        if (RoleRefusal(access, Roles.Viewer) is { } forbidden)
        {
            return forbidden;
        }

        var from = Cursor(after);
        var rows = await data.ReadAsync((db, token) => db.Memberships
            .Where(member => member.OrganizationId == organizationId && member.Role != null && string.Compare(member.UserId, from) > 0)
            .OrderBy(member => member.UserId)
            .Take(limit + 1)
            .Join(db.Users, member => member.UserId, user => user.Id, (member, user) => new { Member = member, user.Email })
            .ToListAsync(token), cancellationToken).ConfigureAwait(false);
        return Paged([.. rows.Select(row => (row.Member, row.Email))], limit, row => row.Member.UserId);
    }

    public async Task<PlanOutcome<MemberEmail>> ChangeRoleAsync(string userId, string organizationId, string memberId, string role, CancellationToken cancellationToken)
    {
        var changed = await this.CommitAsync(async () =>
        {
            var read = await this.OrganizationAccessAsync(userId, organizationId, cancellationToken).ConfigureAwait(false);
            if (read is not OrganizationAccess access)
            {
                return Refused<MembershipRecord>(RefusalOf(read));
            }

            if (RoleRefusal(access, Roles.Admin) is { } forbidden)
            {
                return Refused<MembershipRecord>(forbidden);
            }

            var member = memberId == userId ? access.Membership : await this.MembershipAsync(organizationId, memberId, cancellationToken).ConfigureAwait(false);
            if (member?.Value.Role is not { } current)
            {
                return Refused<MembershipRecord>(NotFoundFailure());
            }

            var owners = current == Roles.Owner || role == Roles.Owner;
            if (owners && RoleRefusal(access, Roles.Owner) is { } ownerForbidden)
            {
                return Refused<MembershipRecord>(ownerForbidden);
            }

            if (current == role)
            {
                return new Plan<MembershipRecord>([], member.Value);
            }

            var organization = access.Organization.Value;
            var count = organization.Owners + (role == Roles.Owner ? 1 : 0) - (current == Roles.Owner ? 1 : 0);
            if (count < 1)
            {
                return Refused<MembershipRecord>(new Conflict("An organization keeps at least one owner.", "last_owner"));
            }

            if (Roles.Rank(role) > Roles.Rank(current) && await this.FencedAsync(organizationId, null, memberId, cancellationToken).ConfigureAwait(false))
            {
                return Refused<MembershipRecord>(UnavailableFailure());
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
        }, cancellationToken).ConfigureAwait(false);
        return changed switch
        {
            MembershipRecord record => new MemberEmail(record, (await this.WithEmailsAsync([record], member => member.UserId, cancellationToken).ConfigureAwait(false)).Single().Email),
            Failure failure => failure,
        };
    }

    public Task<PlanOutcome<Done>> RemoveAsync(string userId, string organizationId, string memberId, CancellationToken cancellationToken) =>
        this.CommitAsync(async () =>
        {
            var leaving = userId == memberId;
            var read = await this.OrganizationAccessAsync(userId, organizationId, requireMfa: !leaving, cancellationToken).ConfigureAwait(false);
            if (read is not OrganizationAccess access)
            {
                return Refused<Done>(RefusalOf(read));
            }

            if (!leaving && RoleRefusal(access, Roles.Admin) is { } adminForbidden)
            {
                return Refused<Done>(adminForbidden);
            }

            var member = leaving ? access.Membership : await this.MembershipAsync(organizationId, memberId, cancellationToken).ConfigureAwait(false);
            if (member is null)
            {
                return Refused<Done>(NotFoundFailure());
            }

            var owner = member.Value.Role == Roles.Owner;
            if (owner && !leaving && RoleRefusal(access, Roles.Owner) is { } ownerForbidden)
            {
                return Refused<Done>(ownerForbidden);
            }

            if (owner && access.Organization.Value.Owners <= 1)
            {
                return Refused<Done>(new Conflict("An organization keeps at least one owner.", "last_owner"));
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

            changes.AddRange((await this.GrantsAsync(memberId, organizationId, cancellationToken).ConfigureAwait(false)).Select(grant => Change.Delete(grant)));
            changes.Add(this.Audit(organizationId, userId, leaving ? "member.left" : "member.removed", memberId));
            return new Plan<Done>(changes, new Done());
        }, cancellationToken);

    internal async Task<(IReadOnlyList<string> Sole, IReadOnlyList<string> Shared)> AccountScopeAsync(string userId, CancellationToken cancellationToken)
    {
        var (sole, shared) = (new List<string>(), new List<string>());
        var memberships = await data.ReadAsync((db, token) => db.Memberships.Where(membership => membership.UserId == userId)
            .Select(membership => new
            {
                Membership = membership,
                Others = membership.Role != null && db.Memberships.Any(other => other.OrganizationId == membership.OrganizationId && other.UserId != userId && other.Role != null),
                Owners = db.Organizations.Where(organization => organization.Id == membership.OrganizationId).Select(organization => (int?)organization.Owners).FirstOrDefault(),
            }).ToListAsync(token), cancellationToken).ConfigureAwait(false);
        foreach (var row in memberships)
        {
            if (row.Membership.Role == Roles.Owner && !row.Others)
            {
                sole.Add(row.Membership.OrganizationId);
                continue;
            }

            if (row.Membership.Role == Roles.Owner && row.Owners is <= 1)
            {
                throw Conflict("last_owner", "Make another member an owner of every organization you share before deleting your account.");
            }

            shared.Add(row.Membership.OrganizationId);
        }

        if (sole.Contains(PlatformBootstrap.OrganizationId))
        {
            throw Conflict("platform_project_protected", "TerraScale's own organization holds its services' databases and cannot be deleted; give it another owner before deleting your account.");
        }

        return (sole, shared);
    }

    internal Task<List<string>> ProjectIdsAsync(string organizationId, CancellationToken cancellationToken) =>
        data.ReadAsync((db, token) => db.Projects.Where(project => project.OrganizationId == organizationId).OrderBy(project => project.Id)
            .Select(project => project.Id).ToListAsync(token), cancellationToken);

    internal async Task<Change?> OwnerAssertionAsync(string userId, string organizationId, CancellationToken cancellationToken) =>
        await this.MembershipAsync(organizationId, userId, cancellationToken).ConfigureAwait(false) is { Value.Role: Roles.Owner } membership
            ? Change.Assert(membership)
            : null;

    public Task<PlanOutcome<ProjectRecord>> CreateProjectAsync(string userId, string organizationId, string name, CancellationToken cancellationToken) =>
        this.CommitAsync(async () =>
        {
            var read = await this.OrganizationAccessAsync(userId, organizationId, cancellationToken).ConfigureAwait(false);
            if (read is not OrganizationAccess access)
            {
                return Refused<ProjectRecord>(RefusalOf(read));
            }

            if (RoleRefusal(access, Roles.Member) is { } forbidden)
            {
                return Refused<ProjectRecord>(forbidden);
            }

            if (await this.FencedAsync(organizationId, null, null, cancellationToken).ConfigureAwait(false))
            {
                return Refused<ProjectRecord>(UnavailableFailure());
            }

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

    public async Task<PlanOutcome<Page<(ProjectRecord Project, string Role)>>> ProjectsAsync(string userId, string organizationId, int limit, string? after, CancellationToken cancellationToken)
    {
        var read = await this.OrganizationAccessAsync(userId, organizationId, cancellationToken).ConfigureAwait(false);
        if (read is not OrganizationAccess access)
        {
            return RefusalOf(read);
        }

        var from = Cursor(after);
        if (access.Role is { } role)
        {
            var rows = await data.ReadAsync((db, token) => db.Projects
                .Where(project => project.OrganizationId == organizationId && string.Compare(project.Id, from) > 0)
                .OrderBy(project => project.Id)
                .Take(limit + 1)
                .Select(project => new
                {
                    Project = project,
                    Grant = db.ProjectMembers.Where(grant => grant.ProjectId == project.Id && grant.UserId == userId).Select(grant => grant.Role).FirstOrDefault(),
                })
                .ToListAsync(token), cancellationToken).ConfigureAwait(false);
            return Paged([.. rows.Select(row => (row.Project, Roles.Higher(Roles.Implied(role), row.Grant)!))], limit, row => row.Project.Id);
        }

        var grants = await data.ReadAsync((db, token) => db.ProjectMembers
            .Where(grant => grant.UserId == userId && grant.OrganizationId == organizationId && string.Compare(grant.ProjectId, from) > 0)
            .OrderBy(grant => grant.ProjectId)
            .Take(limit + 1)
            .Join(db.Projects, grant => grant.ProjectId, project => project.Id, (grant, project) => new { Project = project, grant.Role })
            .ToListAsync(token), cancellationToken).ConfigureAwait(false);
        return Paged([.. grants.Select(row => (row.Project, row.Role))], limit, row => row.Project.Id);
    }

    public Task<ProjectAccessResult> ProjectAccessAsync(string userId, string organizationId, string projectId, CancellationToken cancellationToken) =>
        this.ProjectAccessAsync(userId, organizationId, projectId, requireMfa: true, cancellationToken);

    internal async Task<ProjectAccess> RequireProjectAsync(string userId, string organizationId, string projectId, CancellationToken cancellationToken) =>
        await this.ProjectAccessAsync(userId, organizationId, projectId, requireMfa: true, cancellationToken).ConfigureAwait(false) switch
        {
            ProjectAccess access => access,
            Failure failure => throw ToException(failure),
        };

    private async Task<ProjectAccessResult> ProjectAccessAsync(string userId, string organizationId, string projectId, bool requireMfa, CancellationToken cancellationToken)
    {
        if (!IsId(organizationId) || !IsId(projectId))
        {
            return NotFoundFailure();
        }

        var row = await data.ReadAsync((db, token) => (
            from project in db.Projects.Where(project => project.Id == projectId && project.OrganizationId == organizationId && project.InstanceId == Instance)
            join organization in db.Organizations on project.OrganizationId equals organization.Id
            from membership in db.Memberships.Where(membership => membership.OrganizationId == organizationId && membership.UserId == userId).DefaultIfEmpty()
            from grant in db.ProjectMembers.Where(grant => grant.ProjectId == projectId && grant.UserId == userId).DefaultIfEmpty()
            select new
            {
                Project = project,
                ProjectVersion = EF.Property<string>(project, AuthDbContext.VersionProperty),
                Organization = organization,
                OrganizationVersion = EF.Property<string>(organization, AuthDbContext.VersionProperty),
                Membership = membership,
                MembershipVersion = EF.Property<string?>(membership, AuthDbContext.VersionProperty),
                Grant = grant,
                GrantVersion = EF.Property<string?>(grant, AuthDbContext.VersionProperty),
            }).SingleOrDefaultAsync(token), cancellationToken).ConfigureAwait(false);
        var member = row?.Membership is { } held ? new Versioned<MembershipRecord>(held, row.MembershipVersion!) : null;
        var granted = row?.Grant is { } given ? new Versioned<ProjectMemberRecord>(given, row.GrantVersion!) : null;
        if (row is null || member?.Value.Role is null && granted is null)
        {
            return NotFoundFailure();
        }

        if (requireMfa)
        {
            if (await this.RequireMfaAsync(row.Organization, userId, cancellationToken).ConfigureAwait(false) is { } mfa)
            {
                return mfa;
            }
        }

        return new ProjectAccess(member, new(row.Organization, row.OrganizationVersion), new(row.Project, row.ProjectVersion), granted);
    }

    public Task<PlanOutcome<ProjectAccess>> RenameProjectAsync(string userId, string organizationId, string projectId, string name, CancellationToken cancellationToken) =>
        this.CommitAsync(async () =>
        {
            var read = await this.ProjectAccessAsync(userId, organizationId, projectId, cancellationToken).ConfigureAwait(false);
            if (read is not ProjectAccess access)
            {
                return Refused<ProjectAccess>(RefusalOf(read));
            }

            if (RoleRefusal(access, Roles.Admin) is { } forbidden)
            {
                return Refused<ProjectAccess>(forbidden);
            }

            if (await this.FencedAsync(organizationId, projectId, null, cancellationToken).ConfigureAwait(false))
            {
                return Refused<ProjectAccess>(UnavailableFailure());
            }

            var renamed = access.Project.Value with { Name = name };
            return new Plan<ProjectAccess>(
            [
                Authority(access, Roles.Admin),
                Change.Update(access.Project, renamed),
                this.Audit(organizationId, userId, "project.renamed", projectId, name),
            ], access with { Project = access.Project with { Value = renamed } }, token => this.NameTakenAsync(organizationId, projectId, name, token));
        }, cancellationToken);

    public Task<PlanOutcome<ProjectAccess>> SetProjectAllowedCidrsAsync(string userId, string organizationId, string projectId, IReadOnlyList<string> allowedCidrs, CancellationToken cancellationToken) =>
        this.CommitAsync(async () =>
        {
            var read = await this.ProjectAccessAsync(userId, organizationId, projectId, cancellationToken).ConfigureAwait(false);
            if (read is not ProjectAccess access)
            {
                return Refused<ProjectAccess>(RefusalOf(read));
            }

            if (RoleRefusal(access, Roles.Admin) is { } forbidden)
            {
                return Refused<ProjectAccess>(forbidden);
            }

            if (await this.FencedAsync(organizationId, projectId, null, cancellationToken).ConfigureAwait(false))
            {
                return Refused<ProjectAccess>(UnavailableFailure());
            }

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
        return await this.CommitAsync(async () =>
        {
            var read = await this.ProjectAccessAsync(userId, organizationId, projectId, requireMfa: accountId is null, cancellationToken).ConfigureAwait(false);
            if (read is not ProjectAccess access)
            {
                return Refused<bool>(RefusalOf(read));
            }

            if (RoleRefusal(access, Roles.Admin) is { } forbidden)
            {
                return Refused<bool>(forbidden);
            }

            if (projectId is PlatformBootstrap.AuthProjectId or PlatformBootstrap.DashboardProjectId)
            {
                return Refused<bool>(PlatformProjectRefusal());
            }

            var account = accountId is null ? null : await this.ClosureAsync("account", accountId, cancellationToken).ConfigureAwait(false);
            if (accountId is not null && account is null)
            {
                throw ApiException.Unavailable();
            }

            if (await this.ClosureAsync("project", projectId, cancellationToken).ConfigureAwait(false) is { } existing)
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
        }, cancellationToken).ConfigureAwait(false) switch
        {
            bool done => done && await this.ClosureAsync("project", projectId, cancellationToken).ConfigureAwait(false)
                is { } job && job.Value.OrganizationId == organizationId ? job : throw ApiException.Unavailable(),
            Failure failure => throw ToException(failure),
        };
    }

    internal async Task<Versioned<ClosureJob>?> ClosureAsync(string scope, string subjectId, CancellationToken cancellationToken)
    {
        var row = await data.ReadAsync((db, token) => db.ClosureJobs.Where(job => job.InstanceId == Instance && job.Scope == scope && job.SubjectId == subjectId)
            .Select(job => new
            {
                Job = job,
                Version = EF.Property<string>(job, AuthDbContext.VersionProperty),
                Committed = db.ClosureCommits.Where(commit => commit.Generation == job.Generation).Select(commit => (DateTimeOffset?)commit.CommittedAt).FirstOrDefault(),
            }).SingleOrDefaultAsync(token), cancellationToken).ConfigureAwait(false);
        if (row is null || !IsId(row.Job.Id))
        {
            return null;
        }

        var job = scope == "account" && row.Job.CommittedAt is null && row.Committed is { } committed ? row.Job with { CommittedAt = committed } : row.Job;
        return new(job, row.Version);
    }

    internal async Task<bool> CancelClosureAsync(Versioned<ClosureJob> job, CancellationToken cancellationToken, string? unlessAccount = null)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (job.Value.Scope == "account")
        {
            return await this.CancelAccountClosureAsync(job.Value, cancellationToken).ConfigureAwait(false) switch
            {
                "cancelled" => true,
                "committed" => false,
                _ => throw ApiException.Unavailable(),
            };
        }

        if (unlessAccount is not null && await this.ClosureAsync("account", unlessAccount, cancellationToken).ConfigureAwait(false) is not null)
        {
            return false;
        }

        return await data.SaveAsync(Change.Delete(job), cancellationToken).ConfigureAwait(false);
    }

    internal async Task<string> CancelAccountClosureAsync(ClosureJob job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        var generation = job.Generation ?? throw new InvalidOperationException("An account closure needs a generation.");
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var state = await data.ReadAsync(async (db, token) => new
            {
                Committed = await db.ClosureCommits.AnyAsync(commit => commit.Generation == generation, token).ConfigureAwait(false),
                Cancelling = await db.ClosureCancellations.AnyAsync(mark => mark.Generation == generation, token).ConfigureAwait(false),
            }, cancellationToken).ConfigureAwait(false);
            if (state.Committed)
            {
                return "committed";
            }

            var current = await this.ClosureAsync("account", job.SubjectId, cancellationToken).ConfigureAwait(false);
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

            if (await data.SaveAsync(changes, cancellationToken).ConfigureAwait(false))
            {
                return "cancelled";
            }
        }

        throw ApiException.Unavailable();
    }

    internal Task<bool> IsClosureCancellingAsync(ClosureJob job, CancellationToken cancellationToken) =>
        job.Generation is { } generation
            ? data.ReadAsync((db, token) => db.ClosureCancellations.AnyAsync(mark => mark.Generation == generation, token), cancellationToken)
            : Task.FromResult(false);

    internal async Task<string?> CommitAccountClosureAsync(Versioned<ClosureJob> job, ClosureJob committed, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(committed);
        if (committed.Scope != "account" || committed.Generation is not { } generation || committed.CommittedAt is not { } at)
        {
            throw new ArgumentException("A committed account generation is required.", nameof(committed));
        }

        if (await this.IsClosureCancellingAsync(committed, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var update = Change.Update(job, committed);
        return await data.SaveAsync([update, Change.Insert(new ClosureCommit(generation, at))], cancellationToken).ConfigureAwait(false) ? update.Version : null;
    }

    internal async Task<bool> RemoveClosedProjectAsync(Versioned<ClosureJob> job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        var (projectId, organizationId) = (job.Value.SubjectId, job.Value.OrganizationId!);
        var removedOutcome = await this.CommitAsync(async () =>
        {
            if (await this.ClosureAsync("project", projectId, cancellationToken).ConfigureAwait(false) is not { } current || !current.Value.Same(job.Value))
            {
                return new Plan<bool>([], await this.ProjectAsync(projectId, cancellationToken).ConfigureAwait(false) is null);
            }

            List<Change> changes = [Change.Delete(current)];
            if (await this.ProjectAsync(projectId, cancellationToken).ConfigureAwait(false) is { } project && project.Value.OrganizationId == organizationId
                && await this.OrganizationAsync(organizationId, cancellationToken).ConfigureAwait(false) is { } organization)
            {
                changes.Add(Change.Update(organization, organization.Value with { Projects = organization.Value.Projects - 1 }));
                changes.Add(Change.Delete(project));
                changes.Add(DeletionJob.Enqueue(Instance, "project", projectId, clock, organizationId));
                if (await users.GetAsync(Instance, job.Value.RequestedBy, cancellationToken).ConfigureAwait(false) is not null)
                {
                    changes.Add(this.Audit(organizationId, job.Value.RequestedBy, "project.deleted", projectId, project.Value.Name));
                }
            }

            return new Plan<bool>(changes, true);
        }, cancellationToken).ConfigureAwait(false);
        if (removedOutcome is Failure removedFailure)
        {
            throw ToException(removedFailure);
        }

        if (removedOutcome is not bool removed || !removed)
        {
            return false;
        }

        await this.RetireCollaboratorsAsync(organizationId, cancellationToken).ConfigureAwait(false);
        return true;
    }

    internal async Task RetireCollaboratorsAsync(string organizationId, CancellationToken cancellationToken)
    {
        try
        {
            var orphans = await data.ReadAsync((db, token) => db.Memberships
                .Where(member => member.OrganizationId == organizationId && member.Role == null
                    && !db.ProjectMembers.Any(grant => grant.OrganizationId == organizationId && grant.UserId == member.UserId))
                .WithVersion().ToListAsync(token), cancellationToken).ConfigureAwait(false);
            foreach (var orphan in orphans)
            {
                await data.SaveAsync(Change.Delete(orphan), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception error) when (error is HttpRequestException or TimeoutException or ApiException or DbUpdateException)
        {
            logger.ZLogWarning($"Cleaning up collaborators after a project deletion failed ({error.GetType().Name:@Error}); what is left grants nothing");
        }
    }

    public async Task<PlanOutcome<Page<(ProjectMemberRecord Member, string Email)>>> ProjectMembersAsync(string userId, string organizationId, string projectId, int limit, string? after, CancellationToken cancellationToken)
    {
        var read = await this.ProjectAccessAsync(userId, organizationId, projectId, cancellationToken).ConfigureAwait(false);
        if (read is not ProjectAccess)
        {
            return RefusalOf(read);
        }

        var from = Cursor(after);
        var rows = await data.ReadAsync((db, token) => db.ProjectMembers
            .Where(grant => grant.ProjectId == projectId && string.Compare(grant.UserId, from) > 0)
            .OrderBy(grant => grant.UserId)
            .Take(limit + 1)
            .Join(db.Users, grant => grant.UserId, user => user.Id, (grant, user) => new { Grant = grant, user.Email })
            .ToListAsync(token), cancellationToken).ConfigureAwait(false);
        return Paged([.. rows.Select(row => (row.Grant, row.Email))], limit, row => row.Grant.UserId);
    }

    public async Task<PlanOutcome<ProjectMemberEmail>> SetProjectRoleAsync(string userId, string organizationId, string projectId, string memberId, string role, CancellationToken cancellationToken)
    {
        var granted = await this.CommitAsync(async () =>
        {
            var read = await this.ProjectAccessAsync(userId, organizationId, projectId, cancellationToken).ConfigureAwait(false);
            if (read is not ProjectAccess access)
            {
                return Refused<ProjectMemberRecord>(RefusalOf(read));
            }

            if (RoleRefusal(access, Roles.Admin) is { } forbidden)
            {
                return Refused<ProjectMemberRecord>(forbidden);
            }

            var self = memberId == userId;
            var member = self ? access.Membership : await this.MembershipAsync(organizationId, memberId, cancellationToken).ConfigureAwait(false);
            var existing = self ? access.Grant : await this.ProjectMemberAsync(projectId, memberId, cancellationToken).ConfigureAwait(false);
            if (member is null || existing is null && member.Value.Role is null)
            {
                return Refused<ProjectMemberRecord>(NotFoundFailure());
            }

            var lowers = existing is not null && Roles.Rank(role) < Roles.Rank(existing.Value.Role);
            if (!lowers && await this.FencedAsync(organizationId, projectId, memberId, cancellationToken).ConfigureAwait(false))
            {
                return Refused<ProjectMemberRecord>(UnavailableFailure());
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
        }, cancellationToken).ConfigureAwait(false);
        return granted switch
        {
            ProjectMemberRecord record => new ProjectMemberEmail(record, (await this.WithEmailsAsync([record], item => item.UserId, cancellationToken).ConfigureAwait(false)).Single().Email),
            Failure failure => failure,
        };
    }

    public Task<PlanOutcome<Done>> RemoveProjectRoleAsync(string userId, string organizationId, string projectId, string memberId, CancellationToken cancellationToken) =>
        this.CommitAsync(async () =>
        {
            var read = await this.ProjectAccessAsync(userId, organizationId, projectId, cancellationToken).ConfigureAwait(false);
            if (read is not ProjectAccess access)
            {
                return Refused<Done>(RefusalOf(read));
            }

            if (RoleRefusal(access, Roles.Admin) is { } forbidden)
            {
                return Refused<Done>(forbidden);
            }

            var self = memberId == userId;
            var existing = self ? access.Grant : await this.ProjectMemberAsync(projectId, memberId, cancellationToken).ConfigureAwait(false);
            if (existing is null)
            {
                return Refused<Done>(NotFoundFailure());
            }

            var member = self ? access.Membership : await this.MembershipAsync(organizationId, memberId, cancellationToken).ConfigureAwait(false);
            List<Change> changes = [Authority(access, Roles.Admin), Change.Delete(existing)];
            if (member is not null)
            {
                changes.Add(member.Value.Role is null && !await this.CollaboratesAsync(memberId, organizationId, projectId, cancellationToken).ConfigureAwait(false)
                    ? Change.Delete(member)
                    : Change.Update(member, member.Value));
            }

            changes.Add(this.Audit(organizationId, userId, "project_member.removed", memberId, projectId));
            return new Plan<Done>(changes, new Done());
        }, cancellationToken);

    public async Task<bool> AddMemberAsync(string organizationId, string userId, string role, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            if (await this.JoinAsync(organizationId, userId, role, cancellationToken).ConfigureAwait(false) is not { } changes)
            {
                return false;
            }

            if (await data.SaveAsync(changes, cancellationToken).ConfigureAwait(false))
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
            if (await this.GrantOperationsAsync(projectId, userId, role, refuseHeld: false, cancellationToken).ConfigureAwait(false) is not { } changes)
            {
                return false;
            }

            if (await data.SaveAsync(changes, cancellationToken).ConfigureAwait(false))
            {
                return true;
            }
        }

        throw ApiException.Unavailable();
    }

    internal async Task<List<Change>?> JoinAsync(string organizationId, string userId, string role, CancellationToken cancellationToken)
    {
        if (await this.OrganizationAsync(organizationId, cancellationToken).ConfigureAwait(false) is not { } organization
            || await this.MembershipAsync(organizationId, userId, cancellationToken).ConfigureAwait(false) is var existing && existing?.Value.Role is not null
            || await users.GetAsync(Instance, userId, cancellationToken).ConfigureAwait(false) is not { } user)
        {
            return null;
        }

        await this.FenceAsync(organizationId, null, userId, cancellationToken).ConfigureAwait(false);
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
        if (await this.ProjectAsync(projectId, cancellationToken).ConfigureAwait(false) is not { } project
            || await users.GetAsync(Instance, userId, cancellationToken).ConfigureAwait(false) is not { } user)
        {
            return null;
        }

        var organizationId = project.Value.OrganizationId;
        var member = await this.MembershipAsync(organizationId, userId, cancellationToken).ConfigureAwait(false);
        var existing = await this.ProjectMemberAsync(projectId, userId, cancellationToken).ConfigureAwait(false);
        if (refuseHeld && Roles.Rank(Roles.Higher(Roles.Implied(member?.Value.Role), existing?.Value.Role)) >= Roles.Rank(role))
        {
            throw Conflict("already_member", "You already have this access to the project.");
        }

        await this.FenceAsync(organizationId, projectId, userId, cancellationToken).ConfigureAwait(false);
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

    public async Task<PlanOutcome<OwnershipTransferRecord>> TransferAsync(string userId, string organizationId, CancellationToken cancellationToken)
    {
        var read = await this.OrganizationAccessAsync(userId, organizationId, cancellationToken).ConfigureAwait(false);
        if (read is not OrganizationAccess access)
        {
            return RefusalOf(read);
        }

        if (RoleRefusal(access, Roles.Viewer) is { } forbidden)
        {
            return forbidden;
        }

        return await this.LiveTransferAsync(access.Organization, cancellationToken).ConfigureAwait(false) is { } live
            ? live.Transfer
            : NotFoundFailure();
    }

    public Task<PlanOutcome<OwnershipTransferRecord>> StartTransferAsync(string userId, string organizationId, string nomineeId, CancellationToken cancellationToken) =>
        this.CommitAsync(async () =>
        {
            var read = await this.OrganizationAccessAsync(userId, organizationId, cancellationToken).ConfigureAwait(false);
            if (read is not OrganizationAccess access)
            {
                return Refused<OwnershipTransferRecord>(RefusalOf(read));
            }

            if (RoleRefusal(access, Roles.Owner) is { } forbidden)
            {
                return Refused<OwnershipTransferRecord>(forbidden);
            }

            var nominee = await this.MembershipAsync(organizationId, nomineeId, cancellationToken).ConfigureAwait(false);
            if (nominee?.Value.Role is not { } role)
            {
                return Refused<OwnershipTransferRecord>(NotFoundFailure());
            }

            if (role == Roles.Owner)
            {
                return Refused<OwnershipTransferRecord>(new Validation("The nominee is already an owner."));
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

    public Task<PlanOutcome<Done>> CancelTransferAsync(string userId, string organizationId, CancellationToken cancellationToken) =>
        this.CommitAsync(async () =>
        {
            var read = await this.OrganizationAccessAsync(userId, organizationId, cancellationToken).ConfigureAwait(false);
            if (read is not OrganizationAccess access)
            {
                return Refused<Done>(RefusalOf(read));
            }

            if (RoleRefusal(access, Roles.Viewer) is { } forbidden)
            {
                return Refused<Done>(forbidden);
            }

            var live = await this.LiveTransferAsync(access.Organization, cancellationToken).ConfigureAwait(false);
            if (live is null)
            {
                return Refused<Done>(NotFoundFailure());
            }

            var nominee = live.Transfer.ToUserId == userId;
            if (!nominee && RoleRefusal(access, Roles.Owner) is { } ownerForbidden)
            {
                return Refused<Done>(ownerForbidden);
            }

            return new Plan<Done>(
            [
                Authority(access),
                Change.Update(access.Organization, access.Organization.Value with { Transfer = null }),
                this.Audit(organizationId, userId, "ownership.transfer_cancelled", live.Transfer.ToUserId),
            ], new Done());
        }, cancellationToken);

    public async Task<PlanOutcome<MemberEmail>> ConfirmTransferAsync(string userId, string organizationId, CancellationToken cancellationToken)
    {
        var confirmed = await this.CommitAsync(async () =>
        {
            var read = await this.OrganizationAccessAsync(userId, organizationId, cancellationToken).ConfigureAwait(false);
            if (read is not OrganizationAccess access)
            {
                return Refused<MembershipRecord>(RefusalOf(read));
            }

            var live = await this.LiveTransferAsync(access.Organization, cancellationToken).ConfigureAwait(false);
            if (live is null)
            {
                return Refused<MembershipRecord>(NotFoundFailure());
            }

            if (live.Transfer.ToUserId != userId)
            {
                return Refused<MembershipRecord>(ForbiddenFailure());
            }

            if (await this.FencedAsync(organizationId, null, userId, cancellationToken).ConfigureAwait(false))
            {
                return Refused<MembershipRecord>(UnavailableFailure());
            }

            var owner = access.Membership.Value with { Role = Roles.Owner };
            return new Plan<MembershipRecord>(
            [
                Change.Update(access.Organization, access.Organization.Value with { Transfer = null }),
                Change.Update(access.Membership, owner),
                Change.Update(live.Starter, live.Starter.Value with { Role = Roles.Admin }),
                this.Audit(organizationId, userId, "ownership.transfer_confirmed", userId),
            ], owner);
        }, cancellationToken).ConfigureAwait(false);
        return confirmed switch
        {
            MembershipRecord record => new MemberEmail(record, (await this.WithEmailsAsync([record], member => member.UserId, cancellationToken).ConfigureAwait(false)).Single().Email),
            Failure failure => failure,
        };
    }

    public async Task<PlanOutcome<Page<AuditEventRecord>>> AuditEventsAsync(string userId, string organizationId, int limit, string? after, CancellationToken cancellationToken)
    {
        var read = await this.OrganizationAccessAsync(userId, organizationId, cancellationToken).ConfigureAwait(false);
        if (read is not OrganizationAccess access)
        {
            return RefusalOf(read);
        }

        if (RoleRefusal(access, Roles.Admin) is { } forbidden)
        {
            return forbidden;
        }

        var before = after is null ? null : Cursor(after);
        var rows = await data.ReadAsync((db, token) => db.AuditEvents
            .Where(item => item.OrganizationId == organizationId && (before == null || string.Compare(item.Id, before) < 0))
            .OrderByDescending(item => item.Id)
            .Take(limit + 1)
            .ToListAsync(token), cancellationToken).ConfigureAwait(false);
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

    internal static Failure? RoleRefusal(OrganizationAccess access, string minimum) =>
        Roles.Rank(access.Role) < Roles.Rank(minimum) ? ForbiddenFailure() : (Failure?)null;

    internal static Failure? RoleRefusal(ProjectAccess access, string minimum) =>
        Roles.Rank(access.Role) < Roles.Rank(minimum) ? ForbiddenFailure() : (Failure?)null;

    internal static Failure RefusalOf(OrganizationAccessResult access) => access switch
    {
        Failure failure => failure,
        OrganizationAccess => throw new UnreachableException(),
    };

    internal static Failure RefusalOf(ProjectAccessResult access) => access switch
    {
        Failure failure => failure,
        ProjectAccess => throw new UnreachableException(),
    };

    internal static Failure RefusalOf(AuthorityCheck check) => check switch
    {
        Failure failure => failure,
        InvitationAuthority => throw new UnreachableException(),
    };

    internal static Failure RefusalOf(KeyAuthorization authorization) => authorization switch
    {
        Failure failure => failure,
        KeyAuthority => throw new UnreachableException(),
    };

    internal static Failure RefusalOf(Invitations.Decidable decidable) => decidable switch
    {
        Failure failure => failure,
        Versioned<InvitationRecord> => throw new UnreachableException(),
    };

    internal static Plan<T> Refused<T>(Failure refusal) => new([], default!, Refusal: refusal);

    internal static ApiException NotFound() => new(StatusCodes.Status404NotFound, "not_found", "No such resource.");

    internal static ApiException Forbidden() => new(StatusCodes.Status403Forbidden, "forbidden_role", "Your role does not allow this.");

    internal static ApiException Conflict(string code, string message) => new(StatusCodes.Status409Conflict, code, message);

    internal static ApiException ToException(Failure failure) => failure switch
    {
        NotFound e => new(StatusCodes.Status404NotFound, e.Code, e.Message ?? "No such resource."),
        Forbidden e => new(StatusCodes.Status403Forbidden, e.Code, e.Message),
        Conflict e => new(StatusCodes.Status409Conflict, e.Code, e.Message),
        Validation e => new(StatusCodes.Status400BadRequest, e.Code, e.Message),
        Unprocessable e => new(StatusCodes.Status422UnprocessableEntity, e.Code, e.Message),
        PreconditionFailed e => new(StatusCodes.Status412PreconditionFailed, e.Code, e.Message),
        Unauthorized e => new(StatusCodes.Status401Unauthorized, e.Code, e.Message ?? "Unauthorized."),
        QuotaExceeded e => e.RetryAfter is { } wait ? ApiException.RateLimited(e.Message, wait) : new ApiException(StatusCodes.Status429TooManyRequests, e.Code, e.Message),
        Unavailable e => new(StatusCodes.Status503ServiceUnavailable, e.Code, e.Message),
        PayloadTooLarge e => new(StatusCodes.Status413PayloadTooLarge, e.Code, e.Message),
        UnsupportedMediaType e => new(StatusCodes.Status415UnsupportedMediaType, e.Code, e.Message),
        RequestTimeout e => new(StatusCodes.Status408RequestTimeout, e.Code, e.Message),
        Gone e => new(StatusCodes.Status410Gone, e.Code, e.Message),
        OutcomeUnknown e => new(StatusCodes.Status503ServiceUnavailable, e.Code, e.Message),
    };

    internal static ApiException PlatformProjectProtected() =>
        Conflict("platform_project_protected", "This project holds a TerraScale service's own database and cannot be deleted.");

    internal static Failure PlatformProjectRefusal() =>
        new Conflict("This project holds a TerraScale service's own database and cannot be deleted.", "platform_project_protected");

    internal static Failure NotFoundFailure() => new NotFound("No such resource.", "not_found");

    internal static Failure ForbiddenFailure() => new Forbidden("Your role does not allow this.", "forbidden_role");

    internal static Failure UnavailableFailure() => new Unavailable("The storage engine is unavailable; retry shortly.", "temporarily_unavailable",
        new Dictionary<string, string> { ["retry_after"] = "2" });

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

    internal async Task<PlanOutcome<T>> CommitAsync<T>(Func<Task<Plan<T>>> plan, CancellationToken cancellationToken)
    {
        using var home = HomeReads.Begin();
        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            var next = await plan().ConfigureAwait(false);
            if (next.Refusal is { } refused)
            {
                return refused;
            }

            if (next.Operations.Count == 0 || await data.SaveAsync(next.Operations, cancellationToken).ConfigureAwait(false))
            {
                return next.Result;
            }

            if (next.Taken is not null && await next.Taken(cancellationToken).ConfigureAwait(false))
            {
                return next.TakenError ?? new Conflict("The organization already has a project with this name.", "name_taken");
            }
        }

        throw ApiException.Unavailable();
    }

    private async Task RefuseOrganizationCreationAsync(string organizationId, CancellationToken cancellationToken)
    {
        if (await data.ReadAsync(async (db, token) =>
            await db.DeletionJobs.AnyAsync(job => job.InstanceId == Instance && job.Scope == "organization" && job.SubjectId == organizationId, token).ConfigureAwait(false)
            || await db.ClosureJobs.AnyAsync(job => job.InstanceId == Instance && job.Scope == "organization" && job.SubjectId == organizationId, token).ConfigureAwait(false), cancellationToken).ConfigureAwait(false))
        {
            throw Conflict("organization_deleted", "This organization was deleted or is closing. Start a new organization checkout.");
        }
    }

    internal async Task FenceAsync(string? organizationId, string? projectId, string? userId, CancellationToken cancellationToken)
    {
        if (await this.FencedAsync(organizationId, projectId, userId, cancellationToken).ConfigureAwait(false))
        {
            throw ApiException.Unavailable();
        }
    }

    internal async Task<bool> FencedAsync(string? organizationId, string? projectId, string? userId, CancellationToken cancellationToken) =>
        await data.ReadAsync((db, token) => db.ClosureJobs.AnyAsync(job => job.InstanceId == Instance
            && (job.Scope == "organization" && job.SubjectId == organizationId
                || job.Scope == "project" && job.SubjectId == projectId
                || job.Scope == "account" && job.SubjectId == userId), token), cancellationToken).ConfigureAwait(false);

    internal Task<Versioned<OrganizationRecord>?> OrganizationAsync(string organizationId, CancellationToken cancellationToken) =>
        data.ReadAsync((db, token) => db.Organizations.Where(organization => organization.Id == organizationId && organization.InstanceId == Instance)
            .WithVersion().SingleOrDefaultAsync(token), cancellationToken);

    internal async Task<string?> RoleAsync(string organizationId, string userId, CancellationToken cancellationToken) =>
        (await this.MembershipAsync(organizationId, userId, cancellationToken).ConfigureAwait(false))?.Value.Role;

    internal Task<Versioned<ProjectRecord>?> ProjectAsync(string projectId, CancellationToken cancellationToken) =>
        data.ReadAsync((db, token) => db.Projects.Where(project => project.Id == projectId && project.InstanceId == Instance)
            .WithVersion().SingleOrDefaultAsync(token), cancellationToken);

    private Task<bool> NameTakenAsync(string organizationId, string? projectId, string name, CancellationToken cancellationToken)
    {
        var key = name.ToUpperInvariant();
        return data.ReadAsync((db, token) => db.Projects.AnyAsync(project => project.OrganizationId == organizationId && project.Id != projectId
            && EF.Property<string>(project, AuthDbContext.NameKeyProperty) == key, token), cancellationToken);
    }

    private static OrganizationRecord Unoffered(OrganizationRecord organization, string userId, string? role) =>
        organization.Transfer is { } transfer
            && (transfer.FromUserId == userId && role != Roles.Owner || transfer.ToUserId == userId && role is null or Roles.Owner)
                ? organization with { Transfer = null }
                : organization;

    private async Task<LiveTransfer?> LiveTransferAsync(Versioned<OrganizationRecord> organization, CancellationToken cancellationToken)
    {
        if (organization.Value.Transfer is not { } transfer || transfer.ExpiresAt <= clock.GetUtcNow()
            || await this.MembershipAsync(organization.Value.Id, transfer.FromUserId, cancellationToken).ConfigureAwait(false) is not { Value.Role: Roles.Owner } starter
            || await this.MembershipAsync(organization.Value.Id, transfer.ToUserId, cancellationToken).ConfigureAwait(false) is not { Value.Role: Roles.Viewer or Roles.Member or Roles.Admin })
        {
            return null;
        }

        return new LiveTransfer(transfer, starter);
    }

    private Task<bool> CollaboratesAsync(string userId, string organizationId, string? except, CancellationToken cancellationToken) =>
        data.ReadAsync((db, token) => db.ProjectMembers.AnyAsync(grant => grant.UserId == userId && grant.OrganizationId == organizationId && grant.ProjectId != except, token),
            cancellationToken);

    private async Task<IReadOnlyList<(T Item, string Email)>> WithEmailsAsync<T>(IReadOnlyList<T> items, Func<T, string> userId, CancellationToken cancellationToken)
    {
        var ids = items.Select(userId).Distinct(StringComparer.Ordinal).ToArray();
        var emails = await data.ReadAsync((db, token) => db.Users.Where(user => user.InstanceId == Instance && ids.Contains(user.Id))
            .ToDictionaryAsync(user => user.Id, user => user.Email, StringComparer.Ordinal, token), cancellationToken).ConfigureAwait(false);
        return [.. items.Where(item => emails.ContainsKey(userId(item))).Select(item => (item, emails[userId(item)]))];
    }

    private Task<List<Versioned<ProjectMemberRecord>>> GrantsAsync(string userId, string organizationId, CancellationToken cancellationToken) =>
        data.ReadAsync((db, token) => db.ProjectMembers.Where(grant => grant.UserId == userId && grant.OrganizationId == organizationId).WithVersion().ToListAsync(token),
            cancellationToken);

    private Task<Versioned<MembershipRecord>?> MembershipAsync(string organizationId, string userId, CancellationToken cancellationToken) =>
        data.ReadAsync((db, token) => db.Memberships.Where(membership => membership.OrganizationId == organizationId && membership.UserId == userId)
            .WithVersion().SingleOrDefaultAsync(token), cancellationToken);

    private Task<Versioned<ProjectMemberRecord>?> ProjectMemberAsync(string projectId, string userId, CancellationToken cancellationToken) =>
        data.ReadAsync((db, token) => db.ProjectMembers.Where(grant => grant.ProjectId == projectId && grant.UserId == userId)
            .WithVersion().SingleOrDefaultAsync(token), cancellationToken);

    internal sealed record Plan<T>(IReadOnlyList<Change> Operations, T Result, Func<CancellationToken, Task<bool>>? Taken = null, Failure? TakenError = null, Failure? Refusal = null);

    public readonly union PlanOutcome<T>(T, Failure);
}

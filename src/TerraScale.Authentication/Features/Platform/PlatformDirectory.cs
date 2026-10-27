using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Identity;
using TerraScale.Authentication.Features.Mfa;
using TerraScale.Authentication.Features.Oidc;
using TerraScale.Authentication.Store;
using TerraScale.DotNet;
using TerraScale.DotNet.TerraBase;
using ZLogger;

namespace TerraScale.Authentication.Features.Platform;

/// <summary>Organization roles (<c>viewer</c> to <c>owner</c>) and project roles (<c>viewer</c> to <c>admin</c>).</summary>
public static class Roles
{
    /// <summary>Reads.</summary>
    public const string Viewer = "viewer";

    /// <summary>Reads and writes; creates projects.</summary>
    public const string Member = "member";

    /// <summary>Manages members, projects and keys.</summary>
    public const string Admin = "admin";

    /// <summary>Organization only: deletes it and manages owners.</summary>
    public const string Owner = "owner";

    /// <summary>Organization roles, lowest first.</summary>
    public static IReadOnlyList<string> Organization { get; } = [Viewer, Member, Admin, Owner];

    /// <summary>Project roles, lowest first.</summary>
    public static IReadOnlyList<string> Project { get; } = [Viewer, Member, Admin];

    /// <summary>The role's rank, or -1 for none.</summary>
    public static int Rank(string? role) => role switch { Viewer => 0, Member => 1, Admin => 2, Owner => 3, _ => -1 };

    /// <summary>The project role an organization role implies on every project of the organization.</summary>
    public static string? Implied(string? organizationRole) => organizationRole == Owner ? Admin : organizationRole;

    /// <summary>The higher of two roles; either may be none.</summary>
    public static string? Higher(string? first, string? second) => Rank(first) >= Rank(second) ? first : second;
}

/// <summary>A record and the version it was read at.</summary>
/// <typeparam name="T">The record.</typeparam>
/// <param name="Value">The record.</param>
/// <param name="Version">Its TerraBase version.</param>
public sealed record Versioned<T>(T Value, string Version);

/// <summary>A user's place in an organization: a member (with a role) or a collaborator on some of its projects.</summary>
/// <param name="Organization">The organization.</param>
/// <param name="Membership">The user's membership; its role is null for a collaborator.</param>
public sealed record OrganizationAccess(Versioned<OrganizationRecord> Organization, Versioned<MembershipRecord> Membership)
{
    /// <summary>The organization role, or null for a collaborator.</summary>
    public string? Role => this.Membership.Value.Role;
}

/// <summary>A user's effective role on a project.</summary>
/// <param name="Membership">The user's membership in the project's organization, if any.</param>
/// <param name="Organization">The project's organization.</param>
/// <param name="Project">The project.</param>
/// <param name="Grant">The user's explicit project role, if any.</param>
public sealed record ProjectAccess(Versioned<MembershipRecord>? Membership, Versioned<OrganizationRecord> Organization, Versioned<ProjectRecord> Project, Versioned<ProjectMemberRecord>? Grant)
{
    /// <summary>The higher of the implied and the explicit role.</summary>
    public string Role => Roles.Higher(Roles.Implied(this.Membership?.Value.Role), this.Grant?.Value.Role)!;
}

/// <summary>One page of a list.</summary>
/// <typeparam name="T">The item.</typeparam>
/// <param name="Items">At most the requested number of items.</param>
/// <param name="Next">The cursor of the next page, or null on the last one.</param>
public sealed record Page<T>(IReadOnlyList<T> Items, string? Next);

/// <summary>
/// Organizations, organization members, projects and project members of the platform tenant (TS-598). Every check reads
/// TerraBase, so a change applies to the next request on every replica.
/// </summary>
/// <remarks>
/// Each change is one TerraBase transaction of create-only or version-conditional writes that also asserts, at the
/// version the caller's check read, the role which authorized the caller, so a caller demoted meanwhile cannot finish
/// it. Every user with any access to an organization has a membership record (with a null role for a collaborator),
/// and every write that gives or takes a project role rewrites or deletes it, so a removal and a grant never both
/// succeed on stale reads. A lost race is read and checked again, at most four times.
/// </remarks>
public sealed class PlatformDirectory(AuthStore store, Users users, TotpCredentials mfa, TimeProvider clock, ILogger<PlatformDirectory> logger)
{
    /// <summary>The platform tenant; organizations exist only there.</summary>
    public const string Instance = OidcIssuer.InstanceId;

    /// <summary>The 403 code for a member without an active second factor in an organization that requires one.</summary>
    public const string MfaRequiredCode = "mfa_required";

    /// <summary>How long an ownership transfer waits for its nominee.</summary>
    public static readonly TimeSpan TransferLifetime = TimeSpan.FromDays(7);

    private const string ProjectNameKind = "project-name";
    private const int Attempts = 4;

    /// <summary>A trimmed name of 1 to 128 characters with a letter or digit and no control characters, or null.</summary>
    public static string? Name(string? value)
    {
        var trimmed = value?.Trim();
        return trimmed is { Length: >= 1 and <= 128 } && trimmed.Any(char.IsLetterOrDigit) && !trimmed.Any(char.IsControl) ? trimmed : null;
    }

    /// <summary>Whether <paramref name="value"/> is a canonical uppercase ULID.</summary>
    public static bool IsId(string? value) =>
        TerraScaleIds.IsValid(value);

    /// <summary>Creates an organization whose first member, an owner, is <paramref name="user"/>.</summary>
    public async Task<OrganizationRecord> CreateOrganizationAsync(UserRecord user, string name, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        var now = clock.GetUtcNow();
        var organization = new OrganizationRecord(AuthStore.NewId(), Instance, name, user.Id, now, Owners: 1, Projects: 0);
        var membership = new MembershipRecord(Instance, organization.Id, user.Id, Roles.Owner, now);
        // An ID collision aborts the whole transaction.
        return await store.TransactAsync(
        [
            AuthStore.Create(AuthStore.OrganizationCollection, Instance, organization.Id, Serialize(organization, PlatformJson.Default.OrganizationRecord)),
            AuthStore.Create(AuthStore.MembershipCollection, Instance, MembershipKey(organization.Id, user.Id), Serialize(membership, PlatformJson.Default.MembershipRecord)),
            this.Audit(organization.Id, user.Id, "organization.created", null, name),
        ], cancellationToken).ConfigureAwait(false) is null ? organization : throw ApiException.Unavailable();
    }

    /// <summary>
    /// A page of the organizations <paramref name="userId"/> is a member of (with its role) or a collaborator in (null
    /// role), in organization id order. A page may hold fewer items than asked while organizations are being deleted.
    /// </summary>
    public async Task<Page<(OrganizationRecord Organization, string? Role)>> OrganizationsAsync(string userId, int limit, string? after, CancellationToken cancellationToken)
    {
        var page = await this.PageAsync(AuthStore.MembershipCollection, [Equal("/userId", userId)], PlatformJson.Default.MembershipRecord, "/organizationId", limit, after, cancellationToken).ConfigureAwait(false);
        var organizations = await ReadEachAsync(page.Items, async (membership, token) =>
            membership.Value.UserId == userId
            && await this.OrganizationAsync(membership.Value.OrganizationId, token).ConfigureAwait(false) is { } organization
            && (membership.Value.Role is not null || await this.CollaboratesAsync(userId, organization.Value.Id, null, token).ConfigureAwait(false))
                ? ((OrganizationRecord, string?)?)(organization.Value, membership.Value.Role)
                : null, cancellationToken).ConfigureAwait(false);
        return new(organizations, page.Next);
    }

    /// <summary>The caller's place in an organization; 404 when it has none, as for an unknown id.</summary>
    public Task<OrganizationAccess> OrganizationAccessAsync(string userId, string organizationId, CancellationToken cancellationToken) =>
        this.OrganizationAccessAsync(userId, organizationId, requireMfa: true, cancellationToken);

    // Leaving and closing one's own account stay possible without a second factor; nothing else does.
    private async Task<OrganizationAccess> OrganizationAccessAsync(string userId, string organizationId, bool requireMfa, CancellationToken cancellationToken)
    {
        if (!IsId(organizationId))
        {
            throw NotFound();
        }

        // Both reads name their documents up front, so they share one storage round trip.
        var organizationRead = this.OrganizationAsync(organizationId, cancellationToken);
        var membershipRead = this.MembershipAsync(organizationId, userId, cancellationToken);
        await Task.WhenAll(organizationRead, membershipRead).ConfigureAwait(false);
        if (await organizationRead.ConfigureAwait(false) is not { } organization
            || await membershipRead.ConfigureAwait(false) is not { } membership
            || membership.Value.Role is null && !await this.CollaboratesAsync(userId, organizationId, null, cancellationToken).ConfigureAwait(false))
        {
            throw NotFound();
        }

        if (requireMfa)
        {
            await this.RequireMfaAsync(organization.Value, userId, cancellationToken).ConfigureAwait(false);
        }

        return new(organization, membership);
    }

    /// <summary>
    /// Sets whether members need an active second factor to reach the organization; needs owner. Turning it on also
    /// needs the caller's own factor, so the policy never locks out the owner who set it.
    /// </summary>
    public Task<OrganizationRecord> SetMfaPolicyAsync(string userId, string organizationId, bool require, CancellationToken cancellationToken) =>
        this.CommitAsync(async () =>
        {
            var access = await this.OrganizationAccessAsync(userId, organizationId, cancellationToken).ConfigureAwait(false);
            RequireRole(access, Roles.Owner);
            if (require && !(await mfa.StatusAsync(Instance, userId, cancellationToken).ConfigureAwait(false)).Enabled)
            {
                throw Conflict("mfa_not_enrolled", "Turn on two-factor authentication for your own account first.");
            }

            var changed = access.Organization.Value with { RequireMfa = require };
            return new Plan<OrganizationRecord>(
            [
                Authority(access, Roles.Owner),
                AuthStore.Replace(AuthStore.OrganizationCollection, Instance, organizationId, Serialize(changed, PlatformJson.Default.OrganizationRecord), access.Organization.Version),
                this.Audit(organizationId, userId, require ? "organization.mfa_required" : "organization.mfa_optional"),
            ], changed);
        }, cancellationToken);

    /// <summary>Records a change to the user's own second factor in every organization it belongs to; best effort.</summary>
    public async Task AuditFactorAsync(string userId, string action, CancellationToken cancellationToken)
    {
        string? after = null;
        try
        {
            do
            {
                var page = await this.PageAsync(AuthStore.MembershipCollection, [Equal("/userId", userId)], PlatformJson.Default.MembershipRecord, "/organizationId", 100, after, cancellationToken).ConfigureAwait(false);
                foreach (var membership in page.Items.Where(item => item.Value.UserId == userId))
                {
                    await store.TransactAsync([this.Audit(membership.Value.OrganizationId, userId, action, userId)], cancellationToken).ConfigureAwait(false);
                }

                after = page.Next;
            }
            while (after is not null);
        }
        catch (Exception error) when (error is HttpRequestException or TimeoutException or ApiException)
        {
            logger.ZLogWarning($"Could not record {action:@Action} in a user's organizations ({error.GetType().Name:@Error})");
        }
    }

    // A member of an organization that requires a second factor reaches it only with an active factor of its own.
    private async Task RequireMfaAsync(OrganizationRecord organization, string userId, CancellationToken cancellationToken)
    {
        if (organization.RequireMfa && !(await mfa.StatusAsync(Instance, userId, cancellationToken).ConfigureAwait(false)).Enabled)
        {
            throw new ApiException(StatusCodes.Status403Forbidden, MfaRequiredCode, "This organization requires two-factor authentication. Turn it on at /ui/terrascale/security.");
        }
    }

    /// <summary>Renames an organization; needs admin.</summary>
    public Task<OrganizationRecord> RenameOrganizationAsync(string userId, string organizationId, string name, CancellationToken cancellationToken) =>
        this.CommitAsync(async () =>
        {
            var access = await this.OrganizationAccessAsync(userId, organizationId, cancellationToken).ConfigureAwait(false);
            RequireRole(access, Roles.Admin);
            var renamed = access.Organization.Value with { Name = name };
            return new Plan<OrganizationRecord>(
            [
                Authority(access, Roles.Admin),
                AuthStore.Replace(AuthStore.OrganizationCollection, Instance, organizationId, Serialize(renamed, PlatformJson.Default.OrganizationRecord), access.Organization.Version),
                this.Audit(organizationId, userId, "organization.renamed", null, name),
            ], renamed);
        }, cancellationToken);

    /// <summary>Deletes an organization without projects, then its memberships; needs owner.</summary>
    public Task DeleteOrganizationAsync(string userId, string organizationId, CancellationToken cancellationToken) =>
        this.DeleteOrganizationAsync(userId, organizationId, closingAccount: false, cancellationToken);

    /// <summary>As <see cref="DeleteOrganizationAsync(string, string, CancellationToken)"/>; closing the caller's own account needs no second factor.</summary>
    internal async Task DeleteOrganizationAsync(string userId, string organizationId, bool closingAccount, CancellationToken cancellationToken)
    {
        await this.CommitAsync(async () =>
        {
            var access = await this.OrganizationAccessAsync(userId, organizationId, requireMfa: !closingAccount, cancellationToken).ConfigureAwait(false);
            RequireRole(access, Roles.Owner);
            if (access.Organization.Value.Projects > 0)
            {
                throw Conflict("not_empty", "Delete the organization's projects first.");
            }

            return new Plan<bool>(
            [
                Authority(access, Roles.Owner),
                AuthStore.Delete(AuthStore.OrganizationCollection, Instance, organizationId, access.Organization.Version),
                DeletionJob.Enqueue(Instance, "organization", organizationId, clock),
            ], true);
        }, cancellationToken).ConfigureAwait(false);

        // Every check, and every token request of a key, reads the organization first, so what is left behind grants nothing.
        await this.CleanUpAsync(AuthStore.MembershipCollection, [("/organizationId", organizationId)], PlatformJson.Default.MembershipRecord,
            membership => MembershipKey(membership.OrganizationId, membership.UserId), cancellationToken).ConfigureAwait(false);
        await this.CleanUpAsync(AuthStore.ApiKeyCollection, [("/organizationId", organizationId)], PlatformJson.Default.ApiKeyRecord, key => key.Id, cancellationToken).ConfigureAwait(false);
        await this.CleanUpAsync(AuthStore.ApiKeyUsageCollection, [("/organizationId", organizationId)], PlatformJson.Default.ApiKeyUsageRecord, usage => usage.KeyId, cancellationToken).ConfigureAwait(false);
        await this.RetireInvitationsAsync([("/organizationId", organizationId)], cancellationToken).ConfigureAwait(false);
        await this.CleanUpAsync(AuthStore.AuditEventCollection, [("/organizationId", organizationId)], PlatformJson.Default.AuditEventRecord, item => item.Id, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>A page of the organization's members with their emails, in user id order; needs any organization role.</summary>
    public async Task<Page<(MembershipRecord Member, string Email)>> MembersAsync(string userId, string organizationId, int limit, string? after, CancellationToken cancellationToken)
    {
        var access = await this.OrganizationAccessAsync(userId, organizationId, cancellationToken).ConfigureAwait(false);
        RequireRole(access, Roles.Viewer);
        var page = await this.PageAsync(AuthStore.MembershipCollection, [Equal("/organizationId", organizationId), RoleIn(Roles.Organization)],
            PlatformJson.Default.MembershipRecord, "/userId", limit, after, cancellationToken).ConfigureAwait(false);
        var members = page.Items.Select(member => member.Value).Where(member => member.OrganizationId == organizationId && member.Role is not null).ToList();
        return new(await this.WithEmailsAsync(members, member => member.UserId, cancellationToken).ConfigureAwait(false), page.Next);
    }

    /// <summary>
    /// Changes a member's organization role; needs admin, and owner when the member is or becomes an owner. The last
    /// owner cannot be demoted.
    /// </summary>
    public async Task<(MembershipRecord Member, string Email)> ChangeRoleAsync(string userId, string organizationId, string memberId, string role, CancellationToken cancellationToken)
    {
        var changed = await this.CommitAsync(async () =>
        {
            var access = await this.OrganizationAccessAsync(userId, organizationId, cancellationToken).ConfigureAwait(false);
            RequireRole(access, Roles.Admin);
            // The caller's own record is the one its check read, so a demotion since then fails the write.
            var member = memberId == userId ? access.Membership : await this.MembershipAsync(organizationId, memberId, cancellationToken).ConfigureAwait(false);
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

            var updated = member.Value with { Role = role };
            List<StoreOperation> operations =
            [
                Authority(access, owners ? Roles.Owner : Roles.Admin),
                Roles.Rank(role) < Roles.Rank(current)
                    ? AuthStore.Replace(AuthStore.MembershipCollection, Instance, MembershipKey(organizationId, memberId), Serialize(updated, PlatformJson.Default.MembershipRecord), member.Version).AddsNoAccess()
                    : AuthStore.Replace(AuthStore.MembershipCollection, Instance, MembershipKey(organizationId, memberId), Serialize(updated, PlatformJson.Default.MembershipRecord), member.Version),
            ];
            var reshaped = Unoffered(organization with { Owners = count }, memberId, role);
            operations.Add(reshaped != organization
                ? AuthStore.Replace(AuthStore.OrganizationCollection, Instance, organizationId, Serialize(reshaped, PlatformJson.Default.OrganizationRecord), access.Organization.Version)
                // Orders the change against a deletion of the organization, whose memberships are deleted after it.
                : AuthStore.Require(AuthStore.OrganizationCollection, Instance, organizationId, Filter("exists", "/id")));
            operations.Add(this.Audit(organizationId, userId, "member.role_changed", memberId, role));
            return new Plan<MembershipRecord>(operations, updated);
        }, cancellationToken).ConfigureAwait(false);
        return (await this.WithEmailsAsync([changed], member => member.UserId, cancellationToken).ConfigureAwait(false)).Single();
    }

    /// <summary>
    /// Removes a member or collaborator from an organization with every project role it holds there: needs admin, and
    /// owner to remove an owner. With <paramref name="memberId"/> equal to <paramref name="userId"/> the caller leaves,
    /// whatever its role. The last owner cannot leave or be removed.
    /// </summary>
    public Task RemoveAsync(string userId, string organizationId, string memberId, CancellationToken cancellationToken) =>
        this.CommitAsync(async () =>
        {
            var leaving = userId == memberId;
            var access = await this.OrganizationAccessAsync(userId, organizationId, requireMfa: !leaving, cancellationToken).ConfigureAwait(false);
            if (!leaving)
            {
                RequireRole(access, Roles.Admin);
            }

            var member = leaving ? access.Membership : await this.MembershipAsync(organizationId, memberId, cancellationToken).ConfigureAwait(false) ?? throw NotFound();
            var owner = member.Value.Role == Roles.Owner;
            if (owner && !leaving)
            {
                RequireRole(access, Roles.Owner);
            }

            if (owner && access.Organization.Value.Owners <= 1)
            {
                throw Conflict("last_owner", "An organization keeps at least one owner.");
            }

            var operations = new List<StoreOperation>();
            if (!leaving)
            {
                operations.Add(Authority(access, owner ? Roles.Owner : Roles.Admin));
            }

            // Every grant rewrites the membership, so a grant made after this read fails the delete and is seen on retry.
            operations.Add(AuthStore.Delete(AuthStore.MembershipCollection, Instance, MembershipKey(organizationId, memberId), member.Version));
            var reshaped = Unoffered(access.Organization.Value with { Owners = access.Organization.Value.Owners - (owner ? 1 : 0) }, memberId, null);
            if (reshaped != access.Organization.Value)
            {
                operations.Add(AuthStore.Replace(AuthStore.OrganizationCollection, Instance, organizationId, Serialize(reshaped, PlatformJson.Default.OrganizationRecord), access.Organization.Version));
            }

            await foreach (var grant in this.GrantsAsync(memberId, organizationId, cancellationToken).ConfigureAwait(false))
            {
                operations.Add(AuthStore.Delete(AuthStore.ProjectMemberCollection, Instance, ProjectMemberKey(grant.Value.ProjectId, memberId), grant.Version));
            }

            operations.Add(this.Audit(organizationId, userId, leaving ? "member.left" : "member.removed", memberId));
            return new Plan<bool>(operations, true);
        }, cancellationToken);

    /// <summary>
    /// An account's organizations before it closes: those where it is the only member with a role (which close with their
    /// projects) and the others (which it leaves). 409 <c>last_owner</c> while it is the last owner of an organization with
    /// other members. Read again after the closure fences are committed: a member who joined before then makes the
    /// organization shared, and the fences keep anyone from joining later.
    /// </summary>
    internal async Task<(IReadOnlyList<string> Sole, IReadOnlyList<string> Shared)> AccountScopeAsync(string userId, CancellationToken cancellationToken)
    {
        var (sole, shared) = (new List<string>(), new List<string>());
        var memberships = await this.QueryAsync(AuthStore.MembershipCollection, [("/userId", userId)], PlatformJson.Default.MembershipRecord, cancellationToken)
            .Where(membership => membership.Value.UserId == userId).Select(membership => membership.Value).ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var membership in memberships)
        {
            var others = membership.Role is not null && await this.QueryAsync(AuthStore.MembershipCollection, [("/organizationId", membership.OrganizationId)], PlatformJson.Default.MembershipRecord, cancellationToken)
                .AnyAsync(other => other.Value.OrganizationId == membership.OrganizationId && other.Value.UserId != userId && other.Value.Role is not null, cancellationToken).ConfigureAwait(false);
            if (membership.Role == Roles.Owner && !others)
            {
                sole.Add(membership.OrganizationId);
                continue;
            }

            if (membership.Role == Roles.Owner && await this.OrganizationAsync(membership.OrganizationId, cancellationToken).ConfigureAwait(false) is { Value.Owners: <= 1 })
            {
                throw Conflict("last_owner", "Make another member an owner of every organization you share before deleting your account.");
            }

            shared.Add(membership.OrganizationId);
        }

        return (sole, shared);
    }

    /// <summary>The ids of an organization's projects.</summary>
    internal IAsyncEnumerable<string> ProjectIdsAsync(string organizationId, CancellationToken cancellationToken) =>
        this.QueryAsync(AuthStore.ProjectCollection, [("/organizationId", organizationId)], PlatformJson.Default.ProjectRecord, cancellationToken)
            .Where(project => project.Value.OrganizationId == organizationId).Select(project => project.Value.Id);

    /// <summary>Asserts that <paramref name="userId"/> still owns <paramref name="organizationId"/>, at the version read.</summary>
    internal async Task<StoreOperation?> OwnerAssertionAsync(string userId, string organizationId, CancellationToken cancellationToken) =>
        await this.MembershipAsync(organizationId, userId, cancellationToken).ConfigureAwait(false) is { Value.Role: Roles.Owner } membership
            ? AuthStore.Require(AuthStore.MembershipCollection, Instance, MembershipKey(organizationId, userId), Filter("in", RoleList([Roles.Owner])), membership.Version)
            : null;

    /// <summary>Creates a project, whose explicit admin becomes the creator; needs the member organization role.</summary>
    public Task<ProjectRecord> CreateProjectAsync(string userId, string organizationId, string name, CancellationToken cancellationToken) =>
        this.CommitAsync(async () =>
        {
            var access = await this.OrganizationAccessAsync(userId, organizationId, cancellationToken).ConfigureAwait(false);
            RequireRole(access, Roles.Member);
            var now = clock.GetUtcNow();
            var project = new ProjectRecord(AuthStore.NewId(), Instance, organizationId, name, userId, now);
            var grant = new ProjectMemberRecord(Instance, organizationId, project.Id, userId, Roles.Admin, now);
            var organization = access.Organization.Value;
            return new Plan<ProjectRecord>(
            [
                // Rewriting the creator's membership at the version its check read requires its role and orders the grant.
                Touch(access.Membership),
                AuthStore.Replace(AuthStore.OrganizationCollection, Instance, organizationId, Serialize(organization with { Projects = organization.Projects + 1 }, PlatformJson.Default.OrganizationRecord), access.Organization.Version),
                AuthStore.Claim(Instance, ProjectNameKind, ProjectNameKey(organizationId, name), project.Id),
                AuthStore.Create(AuthStore.ProjectCollection, Instance, project.Id, Serialize(project, PlatformJson.Default.ProjectRecord)),
                AuthStore.Create(AuthStore.ProjectMemberCollection, Instance, ProjectMemberKey(project.Id, userId), Serialize(grant, PlatformJson.Default.ProjectMemberRecord)),
                this.Audit(organizationId, userId, "project.created", project.Id, name),
            ], project, Claim: 2);
        }, cancellationToken);

    /// <summary>A page of the projects the caller can reach, in id order, with its effective role on each.</summary>
    public async Task<Page<(ProjectRecord Project, string Role)>> ProjectsAsync(string userId, string organizationId, int limit, string? after, CancellationToken cancellationToken)
    {
        var access = await this.OrganizationAccessAsync(userId, organizationId, cancellationToken).ConfigureAwait(false);
        var projects = new List<(ProjectRecord, string)>();
        if (access.Role is { } role)
        {
            var page = await this.PageAsync(AuthStore.ProjectCollection, [Equal("/organizationId", organizationId)], PlatformJson.Default.ProjectRecord, "/id", limit, after, cancellationToken).ConfigureAwait(false);
            // An organization owner/admin already has the highest project role. An explicit grant cannot raise it.
            if (Roles.Implied(role) == Roles.Admin)
            {
                return new(page.Items.Where(project => project.Value.OrganizationId == organizationId)
                    .Select(project => (project.Value, Roles.Admin)).ToArray(), page.Next);
            }

            // Only this page's grants: one point read per project, never the user's whole grant list.
            projects.AddRange(await ReadEachAsync(page.Items, async (project, token) =>
                project.Value.OrganizationId == organizationId
                    ? ((ProjectRecord, string)?)(project.Value, Roles.Higher(Roles.Implied(role), (await this.ProjectMemberAsync(project.Value.Id, userId, token).ConfigureAwait(false))?.Value.Role)!)
                    : null, cancellationToken).ConfigureAwait(false));
            return new(projects, page.Next);
        }

        var grants = await this.PageAsync(AuthStore.ProjectMemberCollection, [Equal("/userId", userId), Equal("/organizationId", organizationId)],
            PlatformJson.Default.ProjectMemberRecord, "/projectId", limit, after, cancellationToken).ConfigureAwait(false);
        projects.AddRange(await ReadEachAsync(grants.Items, async (grant, token) =>
            grant.Value.UserId == userId && await this.ProjectAsync(grant.Value.ProjectId, token).ConfigureAwait(false) is { } project
            && project.Value.OrganizationId == organizationId
                ? ((ProjectRecord, string)?)(project.Value, grant.Value.Role)
                : null, cancellationToken).ConfigureAwait(false));
        return new(projects, grants.Next);
    }

    /// <summary>The caller's effective role on a project; 404 when it has none, as for an unknown id.</summary>
    public Task<ProjectAccess> ProjectAccessAsync(string userId, string organizationId, string projectId, CancellationToken cancellationToken) =>
        this.ProjectAccessAsync(userId, organizationId, projectId, requireMfa: true, cancellationToken);

    private async Task<ProjectAccess> ProjectAccessAsync(string userId, string organizationId, string projectId, bool requireMfa, CancellationToken cancellationToken)
    {
        if (!IsId(organizationId) || !IsId(projectId))
        {
            throw NotFound();
        }

        // Every read names its document up front, so they share one storage round trip.
        var organizationRead = this.OrganizationAsync(organizationId, cancellationToken);
        var projectRead = this.ProjectAsync(projectId, cancellationToken);
        var membershipRead = this.MembershipAsync(organizationId, userId, cancellationToken);
        var grantRead = this.ProjectMemberAsync(projectId, userId, cancellationToken);
        await Task.WhenAll(organizationRead, projectRead, membershipRead, grantRead).ConfigureAwait(false);
        if (await organizationRead.ConfigureAwait(false) is not { } organization
            || await projectRead.ConfigureAwait(false) is not { } project
            || project.Value.OrganizationId != organizationId)
        {
            throw NotFound();
        }

        var membership = await membershipRead.ConfigureAwait(false);
        var grant = await grantRead.ConfigureAwait(false);
        if (membership?.Value.Role is null && grant is null)
        {
            throw NotFound();
        }

        if (requireMfa)
        {
            await this.RequireMfaAsync(organization.Value, userId, cancellationToken).ConfigureAwait(false);
        }

        return new(membership, organization, project, grant);
    }

    /// <summary>Renames a project; needs the project's admin role. Names stay unique in the organization, ignoring case.</summary>
    public Task<ProjectAccess> RenameProjectAsync(string userId, string organizationId, string projectId, string name, CancellationToken cancellationToken) =>
        this.CommitAsync(async () =>
        {
            var access = await this.ProjectAccessAsync(userId, organizationId, projectId, cancellationToken).ConfigureAwait(false);
            RequireRole(access, Roles.Admin);
            var renamed = access.Project.Value with { Name = name };
            List<StoreOperation> operations =
            [
                Authority(access, Roles.Admin),
                AuthStore.Replace(AuthStore.ProjectCollection, Instance, projectId, Serialize(renamed, PlatformJson.Default.ProjectRecord), access.Project.Version),
            ];
            int? claim = null;
            var before = ProjectNameKey(organizationId, access.Project.Value.Name);
            var after = ProjectNameKey(organizationId, name);
            if (before != after)
            {
                claim = operations.Count;
                operations.Add(AuthStore.Claim(Instance, ProjectNameKind, after, projectId));
                if (await store.FindUniqueAsync(Instance, ProjectNameKind, before, cancellationToken).ConfigureAwait(false) is { } held && held.OwnerId == projectId)
                {
                    operations.Add(AuthStore.Release(Instance, ProjectNameKind, before, held.Version));
                }
            }

            operations.Add(this.Audit(organizationId, userId, "project.renamed", projectId, name));
            return new Plan<ProjectAccess>(operations, access with { Project = access.Project with { Value = renamed } }, claim);
        }, cancellationToken);

    /// <summary>Replaces the project's IP allowlist (empty allows any address); needs its admin role.</summary>
    public Task<ProjectAccess> SetProjectAllowedCidrsAsync(string userId, string organizationId, string projectId, IReadOnlyList<string> allowedCidrs, CancellationToken cancellationToken) =>
        this.CommitAsync(async () =>
        {
            var access = await this.ProjectAccessAsync(userId, organizationId, projectId, cancellationToken).ConfigureAwait(false);
            RequireRole(access, Roles.Admin);
            var changed = access.Project.Value with { AllowedCidrs = allowedCidrs };
            return new Plan<ProjectAccess>(
            [
                Authority(access, Roles.Admin),
                AuthStore.Replace(AuthStore.ProjectCollection, Instance, projectId, Serialize(changed, PlatformJson.Default.ProjectRecord), access.Project.Version),
                this.Audit(organizationId, userId, "project.allowed_cidrs_changed", projectId, string.Join(' ', allowedCidrs)),
            ], access with { Project = access.Project with { Value = changed } });
        }, cancellationToken);

    /// <summary>
    /// Commits the caller's closure intent for a project, needing its admin role in the same transaction, or returns the
    /// pending one (a retry, or another admin's). Nothing outside ts-auth changes here: see <see cref="Closures"/>.
    /// </summary>
    internal async Task<Versioned<ClosureJob>> RequestProjectClosureAsync(string userId, string organizationId, string projectId, string? accountId, CancellationToken cancellationToken)
    {
        await this.CommitAsync(async () =>
        {
            // An account closure (accountId set) closes the caller's own sole organizations, with or without a second factor.
            var access = await this.ProjectAccessAsync(userId, organizationId, projectId, requireMfa: accountId is null, cancellationToken).ConfigureAwait(false);
            RequireRole(access, Roles.Admin);
            if (await this.ClosureAsync("project", projectId, cancellationToken).ConfigureAwait(false) is { } existing)
            {
                if (accountId is not null && existing.Value.ProjectClosureVersion == 1)
                {
                    var adopted = existing.Value with { AccountId = accountId, ProjectClosureVersion = 2, Generation = AuthStore.NewId(), NextAttemptAt = clock.GetUtcNow() };
                    return new Plan<bool>(
                    [
                        Authority(access, Roles.Admin),
                        AuthStore.Require(AuthStore.ClosureJobCollection, Instance, ClosureJob.Key("account", accountId), Filter("exists", "/id")),
                        AuthStore.Replace(AuthStore.ClosureJobCollection, Instance, ClosureJob.Key("project", projectId),
                            JsonSerializer.SerializeToElement(adopted, ClosureJson.Default.ClosureJob), existing.Version),
                    ], true);
                }

                return new Plan<bool>([], true);
            }

            // The project's closure id is drawn at random once and kept on its record, so every later intent (a retry
            // after a refusal included) presents the closure TerraBase may already hold. Nothing derives it from the id.
            var project = access.Project.Value;
            var closureId = project.ClosureId ?? AuthStore.NewId();
            var (_, create) = ClosureJob.New(Instance, "project", projectId, organizationId, userId, clock, accountId, closureId);
            List<StoreOperation> operations =
            [
                Authority(access, Roles.Admin),
                project.ClosureId is null
                    ? AuthStore.Replace(AuthStore.ProjectCollection, Instance, projectId, Serialize(project with { ClosureId = closureId }, PlatformJson.Default.ProjectRecord),
                        access.Project.Version).AddsNoAccess()
                    : AuthStore.Require(AuthStore.ProjectCollection, Instance, projectId, Filter("exists", "/id"), access.Project.Version),
                create,
                this.Audit(organizationId, userId, "project.closure_requested", projectId, access.Project.Value.Name),
            ];
            if (accountId is not null)
            {
                // An account's step creates its project intents only while its account intent exists, so a concurrent
                // cancellation cannot leave one behind without its driver.
                operations.Add(AuthStore.Require(AuthStore.ClosureJobCollection, Instance, ClosureJob.Key("account", accountId), Filter("exists", "/id")));
            }

            return new Plan<bool>(operations, true);
        }, cancellationToken).ConfigureAwait(false);
        return await this.ClosureAsync("project", projectId, cancellationToken).ConfigureAwait(false)
            is { } job && job.Value.OrganizationId == organizationId ? job : throw ApiException.Unavailable();
    }

    /// <summary>The pending closure intent of a scope's subject, or null.</summary>
    internal async Task<Versioned<ClosureJob>?> ClosureAsync(string scope, string subjectId, CancellationToken cancellationToken)
    {
        if (await store.GetAsync(AuthStore.ClosureJobCollection, Instance, ClosureJob.Key(scope, subjectId), cancellationToken).ConfigureAwait(false) is not { } document
            || document.Document.Deserialize(ClosureJson.Default.ClosureJob) is not { } job
            || job.InstanceId != Instance || job.Scope != scope || job.SubjectId != subjectId || !IsId(job.Id))
        {
            return null;
        }

        // A replicated full-document deferral cannot erase evidence of a region's irreversible step.
        if (scope == "account" && job.CommittedAt is null && await store.ClosureCommittedAtAsync(job, cancellationToken).ConfigureAwait(false) is { } committed)
        {
            job = job with { CommittedAt = committed };
        }

        return new(job, document.Version);
    }

    /// <summary>True only after every holder fenced this account generation; a pending barrier returns 503.</summary>
    internal async Task<bool> CancelClosureAsync(Versioned<ClosureJob> job, CancellationToken cancellationToken, string? unlessAccount = null)
    {
        if (job.Value.Scope == "account")
        {
            var answer = await store.CancelAccountClosureAsync(job.Value, cancellationToken).ConfigureAwait(false);
            return answer.Status switch
            {
                "cancelled" => true,
                "committed" => false,
                _ => throw ApiException.Unavailable(),
            };
        }

        return await store.TransactAsync(unlessAccount is null
            ? [AuthStore.Delete(AuthStore.ClosureJobCollection, Instance, ClosureJob.Key(job.Value.Scope, job.Value.SubjectId), job.Version)]
            : [
                AuthStore.Delete(AuthStore.ClosureJobCollection, Instance, ClosureJob.Key(job.Value.Scope, job.Value.SubjectId), job.Version),
                AuthStore.Absent(AuthStore.ClosureJobCollection, Instance, ClosureJob.Key("account", unlessAccount)),
            ], cancellationToken).ConfigureAwait(false) is null;
    }

    /// <summary>
    /// After TerraBase accepted the closure, removes the project root with its logical deletion job and the intent in one
    /// transaction, then its project roles, API keys and invitations. The intent's committed admin role authorizes it.
    /// True when the root is gone; false (nothing cleaned) when this intent is gone but the root survives.
    /// </summary>
    internal async Task<bool> RemoveClosedProjectAsync(Versioned<ClosureJob> job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        var (projectId, organizationId) = (job.Value.SubjectId, job.Value.OrganizationId!);
        var removed = await this.CommitAsync(async () =>
        {
            if (await this.ClosureAsync("project", projectId, cancellationToken).ConfigureAwait(false) is not { } current || !current.Value.Same(job.Value))
            {
                // Not this intent any more: only an absent root is done; a surviving one keeps every record.
                return new Plan<bool>([], await this.ProjectAsync(projectId, cancellationToken).ConfigureAwait(false) is null);
            }

            List<StoreOperation> operations = [AuthStore.Delete(AuthStore.ClosureJobCollection, Instance, ClosureJob.Key("project", projectId), current.Version)];
            if (await this.ProjectAsync(projectId, cancellationToken).ConfigureAwait(false) is { } project && project.Value.OrganizationId == organizationId
                && await this.OrganizationAsync(organizationId, cancellationToken).ConfigureAwait(false) is { } organization)
            {
                operations.Add(AuthStore.Replace(AuthStore.OrganizationCollection, Instance, organizationId,
                    Serialize(organization.Value with { Projects = organization.Value.Projects - 1 }, PlatformJson.Default.OrganizationRecord), organization.Version));
                operations.Add(AuthStore.Delete(AuthStore.ProjectCollection, Instance, projectId, project.Version));
                operations.Add(DeletionJob.Enqueue(Instance, "project", projectId, clock, organizationId));
                var name = ProjectNameKey(organizationId, project.Value.Name);
                if (await store.FindUniqueAsync(Instance, ProjectNameKind, name, cancellationToken).ConfigureAwait(false) is { } held && held.OwnerId == projectId)
                {
                    operations.Add(AuthStore.Release(Instance, ProjectNameKind, name, held.Version));
                }

                // An erased requester's id is fenced from new records; the deletion job keeps the evidence then.
                if (await users.GetAsync(Instance, job.Value.RequestedBy, cancellationToken).ConfigureAwait(false) is not null)
                {
                    operations.Add(this.Audit(organizationId, job.Value.RequestedBy, "project.deleted", projectId, project.Value.Name));
                }
            }

            return new Plan<bool>(operations, true);
        }, cancellationToken).ConfigureAwait(false);
        if (!removed)
        {
            return false;
        }

        // Every check and every token request of a key reads the project first, and a collaborator needs a project that
        // exists, so what is left behind grants nothing; retiring the roles also removes collaborators the project was the
        // last one of.
        try
        {
            await foreach (var grant in this.QueryAsync(AuthStore.ProjectMemberCollection, [("/projectId", projectId)], PlatformJson.Default.ProjectMemberRecord, cancellationToken).ConfigureAwait(false))
            {
                await this.RetireGrantAsync(organizationId, projectId, grant.Value.UserId, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception error) when (error is HttpRequestException or TimeoutException or ApiException)
        {
            logger.ZLogWarning($"Cleaning up {AuthStore.ProjectMemberCollection:@Collection} after a deletion failed ({error.GetType().Name:@Error}); what is left grants nothing");
        }

        await this.CleanUpAsync(AuthStore.ApiKeyCollection, [("/projectId", projectId)], PlatformJson.Default.ApiKeyRecord, key => key.Id, cancellationToken).ConfigureAwait(false);
        await this.CleanUpAsync(AuthStore.ApiKeyUsageCollection, [("/projectId", projectId)], PlatformJson.Default.ApiKeyUsageRecord, usage => usage.KeyId, cancellationToken).ConfigureAwait(false);
        await this.RetireInvitationsAsync([("/projectId", projectId)], cancellationToken).ConfigureAwait(false);
        return true;
    }

    // Deletes a deleted project's role and, as RemoveProjectRoleAsync does, the membership of a collaborator left with
    // no role on a live project. The membership is deleted at the version read, so a grant committed meanwhile (which
    // rewrites it) fails this attempt and the next one keeps it.
    internal Task<bool> RetireGrantAsync(string organizationId, string projectId, string memberId, CancellationToken cancellationToken) =>
        this.CommitAsync(async () =>
        {
            if (await this.ProjectMemberAsync(projectId, memberId, cancellationToken).ConfigureAwait(false) is not { } grant)
            {
                return new Plan<bool>([], true);
            }

            List<StoreOperation> operations = [AuthStore.Delete(AuthStore.ProjectMemberCollection, Instance, ProjectMemberKey(projectId, memberId), grant.Version)];
            if (await this.MembershipAsync(organizationId, memberId, cancellationToken).ConfigureAwait(false) is { Value.Role: null } member
                && !await this.CollaboratesAsync(memberId, organizationId, null, cancellationToken).ConfigureAwait(false))
            {
                operations.Add(AuthStore.Delete(AuthStore.MembershipCollection, Instance, MembershipKey(organizationId, memberId), member.Version));
            }

            return new Plan<bool>(operations, true);
        }, cancellationToken);

    /// <summary>A page of a project's explicit roles with their emails, in user id order; needs an effective project role.</summary>
    public async Task<Page<(ProjectMemberRecord Member, string Email)>> ProjectMembersAsync(string userId, string organizationId, string projectId, int limit, string? after, CancellationToken cancellationToken)
    {
        _ = await this.ProjectAccessAsync(userId, organizationId, projectId, cancellationToken).ConfigureAwait(false);
        var page = await this.PageAsync(AuthStore.ProjectMemberCollection, [Equal("/projectId", projectId)], PlatformJson.Default.ProjectMemberRecord, "/userId", limit, after, cancellationToken).ConfigureAwait(false);
        var grants = page.Items.Select(grant => grant.Value).Where(grant => grant.ProjectId == projectId).ToList();
        return new(await this.WithEmailsAsync(grants, grant => grant.UserId, cancellationToken).ConfigureAwait(false), page.Next);
    }

    /// <summary>
    /// Grants or changes an explicit project role; needs the project's admin role. A new grant needs a member of the
    /// organization (404 otherwise): anyone else is invited.
    /// </summary>
    public async Task<(ProjectMemberRecord Member, string Email)> SetProjectRoleAsync(string userId, string organizationId, string projectId, string memberId, string role, CancellationToken cancellationToken)
    {
        var granted = await this.CommitAsync(async () =>
        {
            var access = await this.ProjectAccessAsync(userId, organizationId, projectId, cancellationToken).ConfigureAwait(false);
            RequireRole(access, Roles.Admin);
            // The caller's own records are the ones its check read, so a demotion since then fails the write.
            var self = memberId == userId;
            var member = self ? access.Membership : await this.MembershipAsync(organizationId, memberId, cancellationToken).ConfigureAwait(false);
            var existing = self ? access.Grant : await this.ProjectMemberAsync(projectId, memberId, cancellationToken).ConfigureAwait(false);
            if (member is null || existing is null && member.Value.Role is null)
            {
                throw NotFound();
            }

            var grant = existing is null ? new ProjectMemberRecord(Instance, organizationId, projectId, memberId, role, clock.GetUtcNow()) : existing.Value with { Role = role };
            var record = Serialize(grant, PlatformJson.Default.ProjectMemberRecord);
            // Lowering an explicit role only takes access away: a pending closure lets it through.
            var lowers = existing is not null && Roles.Rank(role) < Roles.Rank(existing.Value.Role);
            return new Plan<ProjectMemberRecord>(
            [
                Authority(access, Roles.Admin),
                // Orders the grant against a concurrent removal of the member (see RemoveAsync).
                lowers ? Touch(member).AddsNoAccess() : Touch(member),
                AuthStore.Require(AuthStore.ProjectCollection, Instance, projectId, Filter("exists", "/id")),
                existing is null
                    ? AuthStore.Create(AuthStore.ProjectMemberCollection, Instance, ProjectMemberKey(projectId, memberId), record)
                    : lowers
                        ? AuthStore.Replace(AuthStore.ProjectMemberCollection, Instance, ProjectMemberKey(projectId, memberId), record, existing.Version).AddsNoAccess()
                        : AuthStore.Replace(AuthStore.ProjectMemberCollection, Instance, ProjectMemberKey(projectId, memberId), record, existing.Version),
                this.Audit(organizationId, userId, "project_member.role_set", memberId, $"{projectId}/{role}"),
            ], grant);
        }, cancellationToken).ConfigureAwait(false);
        return (await this.WithEmailsAsync([granted], grant => grant.UserId, cancellationToken).ConfigureAwait(false)).Single();
    }

    /// <summary>
    /// Removes an explicit project role; needs the project's admin role. A collaborator left without a project role
    /// leaves the organization.
    /// </summary>
    public Task RemoveProjectRoleAsync(string userId, string organizationId, string projectId, string memberId, CancellationToken cancellationToken) =>
        this.CommitAsync(async () =>
        {
            var access = await this.ProjectAccessAsync(userId, organizationId, projectId, cancellationToken).ConfigureAwait(false);
            RequireRole(access, Roles.Admin);
            var self = memberId == userId;
            var existing = (self ? access.Grant : await this.ProjectMemberAsync(projectId, memberId, cancellationToken).ConfigureAwait(false)) ?? throw NotFound();
            var member = self ? access.Membership : await this.MembershipAsync(organizationId, memberId, cancellationToken).ConfigureAwait(false);
            List<StoreOperation> operations =
            [
                Authority(access, Roles.Admin),
                AuthStore.Delete(AuthStore.ProjectMemberCollection, Instance, ProjectMemberKey(projectId, memberId), existing.Version),
            ];
            if (member is not null)
            {
                operations.Add(member.Value.Role is null && !await this.CollaboratesAsync(memberId, organizationId, projectId, cancellationToken).ConfigureAwait(false)
                    ? AuthStore.Delete(AuthStore.MembershipCollection, Instance, MembershipKey(organizationId, memberId), member.Version)
                    : Touch(member).AddsNoAccess());
            }

            operations.Add(this.Audit(organizationId, userId, "project_member.removed", memberId, projectId));
            return new Plan<bool>(operations, true);
        }, cancellationToken);

    /// <summary>
    /// Makes <paramref name="userId"/> a member of an organization with <paramref name="role"/>; a collaborator keeps its
    /// project roles. False when it already is a member or the organization is gone.
    /// </summary>
    public async Task<bool> AddMemberAsync(string organizationId, string userId, string role, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            if (await this.JoinAsync(organizationId, userId, role, cancellationToken).ConfigureAwait(false) is not { } operations)
            {
                return false;
            }

            if (await store.TransactAsync(operations, cancellationToken).ConfigureAwait(false) is null)
            {
                return true;
            }
        }

        throw ApiException.Unavailable();
    }

    /// <summary>
    /// Gives <paramref name="userId"/> an explicit role on a project (keeping a higher one it holds): an organization
    /// member keeps its organization role, anyone else becomes a collaborator. False when the project is gone.
    /// </summary>
    public async Task<bool> GrantAsync(string projectId, string userId, string role, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            if (await this.GrantOperationsAsync(projectId, userId, role, refuseHeld: false, cancellationToken).ConfigureAwait(false) is not { } operations)
            {
                return false;
            }

            if (await store.TransactAsync(operations, cancellationToken).ConfigureAwait(false) is null)
            {
                return true;
            }
        }

        throw ApiException.Unavailable();
    }

    /// <summary>The writes of <see cref="AddMemberAsync"/>, or null when there is nothing to write.</summary>
    internal async Task<List<StoreOperation>?> JoinAsync(string organizationId, string userId, string role, CancellationToken cancellationToken)
    {
        if (await this.OrganizationAsync(organizationId, cancellationToken).ConfigureAwait(false) is not { } organization
            || await this.MembershipAsync(organizationId, userId, cancellationToken).ConfigureAwait(false) is var existing && existing?.Value.Role is not null)
        {
            return null;
        }

        var membership = new MembershipRecord(Instance, organizationId, userId, role, existing?.Value.JoinedAt ?? clock.GetUtcNow());
        var record = Serialize(membership, PlatformJson.Default.MembershipRecord);
        return
        [
            existing is null
                ? AuthStore.Create(AuthStore.MembershipCollection, Instance, MembershipKey(organizationId, userId), record)
                : AuthStore.Replace(AuthStore.MembershipCollection, Instance, MembershipKey(organizationId, userId), record, existing.Version),
            role == Roles.Owner
                ? AuthStore.Replace(AuthStore.OrganizationCollection, Instance, organizationId, Serialize(organization.Value with { Owners = organization.Value.Owners + 1 }, PlatformJson.Default.OrganizationRecord), organization.Version)
                // Orders the join against a deletion of the organization.
                : AuthStore.Require(AuthStore.OrganizationCollection, Instance, organizationId, Filter("exists", "/id")),
        ];
    }

    /// <summary>
    /// The writes of <see cref="GrantAsync"/>, or null when the project is gone. With <paramref name="refuseHeld"/>, 409
    /// already_member when the user's effective role is already at least <paramref name="role"/>; the writes name the
    /// membership and role it read, so a change to either since fails them and the check runs again.
    /// </summary>
    internal async Task<List<StoreOperation>?> GrantOperationsAsync(string projectId, string userId, string role, bool refuseHeld, CancellationToken cancellationToken)
    {
        if (await this.ProjectAsync(projectId, cancellationToken).ConfigureAwait(false) is not { } project)
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

        var now = clock.GetUtcNow();
        var grant = existing is null ? new ProjectMemberRecord(Instance, organizationId, projectId, userId, role, now) : existing.Value with { Role = Roles.Higher(existing.Value.Role, role)! };
        var record = Serialize(grant, PlatformJson.Default.ProjectMemberRecord);
        return
        [
            // A collaborator's membership has no role; every grant rewrites it (see RemoveAsync).
            member is null
                ? AuthStore.Create(AuthStore.MembershipCollection, Instance, MembershipKey(organizationId, userId), Serialize(new MembershipRecord(Instance, organizationId, userId, null, now), PlatformJson.Default.MembershipRecord))
                : Touch(member),
            AuthStore.Require(AuthStore.ProjectCollection, Instance, projectId, Filter("exists", "/id")),
            existing is null
                ? AuthStore.Create(AuthStore.ProjectMemberCollection, Instance, ProjectMemberKey(projectId, userId), record)
                : AuthStore.Replace(AuthStore.ProjectMemberCollection, Instance, ProjectMemberKey(projectId, userId), record, existing.Version),
        ];
    }

    /// <summary>The live ownership transfer of an organization, or 404; needs any organization role.</summary>
    public async Task<OwnershipTransferRecord> TransferAsync(string userId, string organizationId, CancellationToken cancellationToken)
    {
        var access = await this.OrganizationAccessAsync(userId, organizationId, cancellationToken).ConfigureAwait(false);
        RequireRole(access, Roles.Viewer);
        return (await this.LiveTransferAsync(access.Organization, cancellationToken).ConfigureAwait(false) ?? throw NotFound()).Transfer;
    }

    /// <summary>
    /// Offers the organization's ownership to <paramref name="nomineeId"/>, a member who is not an owner, for seven days;
    /// it replaces a pending offer. Needs owner.
    /// </summary>
    public Task<OwnershipTransferRecord> StartTransferAsync(string userId, string organizationId, string nomineeId, CancellationToken cancellationToken) =>
        this.CommitAsync(async () =>
        {
            var access = await this.OrganizationAccessAsync(userId, organizationId, cancellationToken).ConfigureAwait(false);
            RequireRole(access, Roles.Owner);
            var nominee = await this.MembershipAsync(organizationId, nomineeId, cancellationToken).ConfigureAwait(false);
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
                Authority(access, Roles.Owner),
                AuthStore.Require(AuthStore.MembershipCollection, Instance, MembershipKey(organizationId, nomineeId), Filter("in", RoleList([Roles.Viewer, Roles.Member, Roles.Admin])), nominee.Version),
                AuthStore.Replace(AuthStore.OrganizationCollection, Instance, organizationId, Serialize(access.Organization.Value with { Transfer = transfer }, PlatformJson.Default.OrganizationRecord), access.Organization.Version),
                this.Audit(organizationId, userId, "ownership.transfer_started", nomineeId),
            ], transfer);
        }, cancellationToken);

    /// <summary>Withdraws the pending transfer; an owner or the nominee.</summary>
    public Task CancelTransferAsync(string userId, string organizationId, CancellationToken cancellationToken) =>
        this.CommitAsync(async () =>
        {
            var access = await this.OrganizationAccessAsync(userId, organizationId, cancellationToken).ConfigureAwait(false);
            RequireRole(access, Roles.Viewer);
            var live = await this.LiveTransferAsync(access.Organization, cancellationToken).ConfigureAwait(false) ?? throw NotFound();
            var nominee = live.Transfer.ToUserId == userId;
            if (!nominee)
            {
                RequireRole(access, Roles.Owner);
            }

            return new Plan<bool>(
            [
                Authority(access, nominee ? Roles.Viewer : Roles.Owner),
                AuthStore.Replace(AuthStore.OrganizationCollection, Instance, organizationId, Serialize(access.Organization.Value with { Transfer = null }, PlatformJson.Default.OrganizationRecord), access.Organization.Version),
                this.Audit(organizationId, userId, "ownership.transfer_cancelled", live.Transfer.ToUserId),
            ], true);
        }, cancellationToken);

    /// <summary>The nominee accepts: it becomes an owner and the owner who offered it an admin.</summary>
    public async Task<(MembershipRecord Member, string Email)> ConfirmTransferAsync(string userId, string organizationId, CancellationToken cancellationToken)
    {
        var confirmed = await this.CommitAsync(async () =>
        {
            var access = await this.OrganizationAccessAsync(userId, organizationId, cancellationToken).ConfigureAwait(false);
            var live = await this.LiveTransferAsync(access.Organization, cancellationToken).ConfigureAwait(false) ?? throw NotFound();
            if (live.Transfer.ToUserId != userId)
            {
                throw Forbidden();
            }

            var owner = access.Membership.Value with { Role = Roles.Owner };
            return new Plan<MembershipRecord>(
            [
                // The owner count is unchanged: one owner in, one out.
                AuthStore.Replace(AuthStore.OrganizationCollection, Instance, organizationId, Serialize(access.Organization.Value with { Transfer = null }, PlatformJson.Default.OrganizationRecord), access.Organization.Version),
                AuthStore.Replace(AuthStore.MembershipCollection, Instance, MembershipKey(organizationId, userId), Serialize(owner, PlatformJson.Default.MembershipRecord), access.Membership.Version),
                AuthStore.Replace(AuthStore.MembershipCollection, Instance, MembershipKey(organizationId, live.Starter.Value.UserId),
                    Serialize(live.Starter.Value with { Role = Roles.Admin }, PlatformJson.Default.MembershipRecord), live.Starter.Version),
                this.Audit(organizationId, userId, "ownership.transfer_confirmed", userId),
            ], owner);
        }, cancellationToken).ConfigureAwait(false);
        return (await this.WithEmailsAsync([confirmed], member => member.UserId, cancellationToken).ConfigureAwait(false)).Single();
    }

    /// <summary>A page of the organization's audit events, newest first; needs admin.</summary>
    public async Task<Page<AuditEventRecord>> AuditEventsAsync(string userId, string organizationId, int limit, string? after, CancellationToken cancellationToken)
    {
        var access = await this.OrganizationAccessAsync(userId, organizationId, cancellationToken).ConfigureAwait(false);
        RequireRole(access, Roles.Admin);
        var page = await this.PageAsync(AuthStore.AuditEventCollection, [Equal("/organizationId", organizationId)], PlatformJson.Default.AuditEventRecord, "/id", limit, after, descending: true, cancellationToken).ConfigureAwait(false);
        return new([.. page.Items.Select(item => item.Value).Where(item => item.OrganizationId == organizationId)], page.Next);
    }

    /// <summary>An audit event, written in the same transaction as the change it records.</summary>
    internal StoreOperation Audit(string organizationId, string actor, string action, string? target = null, string? detail = null)
    {
        var id = AuthStore.NewId();
        return AuthStore.Create(AuthStore.AuditEventCollection, Instance, id,
            Serialize(new AuditEventRecord(id, Instance, organizationId, actor, action, target, detail, clock.GetUtcNow()), PlatformJson.Default.AuditEventRecord));
    }

    /// <summary>403 unless the caller's organization role is at least <paramref name="minimum"/>; collaborators have none.</summary>
    public static void RequireRole(OrganizationAccess access, string minimum)
    {
        ArgumentNullException.ThrowIfNull(access);
        if (Roles.Rank(access.Role) < Roles.Rank(minimum))
        {
            throw Forbidden();
        }
    }

    /// <summary>403 unless the caller's effective project role is at least <paramref name="minimum"/>.</summary>
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

    internal static JsonElement Filter(string operation, JsonNode argument)
    {
        using var parsed = JsonDocument.Parse(new JsonObject { [operation] = argument }.ToJsonString());
        return parsed.RootElement.Clone();
    }

    /// <summary>A query condition: the string at <paramref name="field"/> equals <paramref name="value"/>.</summary>
    internal static JsonObject Equal(string field, string value) => new() { ["eq"] = new JsonArray(field, value) };

    // Asserts, inside the transaction, that the organization role which authorized the caller still holds at the
    // version its check read.
    internal static StoreOperation Authority(OrganizationAccess access, string minimum) =>
        AuthStore.Require(AuthStore.MembershipCollection, Instance, MembershipKey(access.Organization.Value.Id, access.Membership.Value.UserId),
            Filter("in", RoleList(Roles.Organization.Where(role => Roles.Rank(role) >= Roles.Rank(minimum)))), access.Membership.Version);

    // The same for a project role: the organization role when it implies enough, otherwise the explicit project role.
    internal static StoreOperation Authority(ProjectAccess access, string minimum) =>
        Roles.Rank(Roles.Implied(access.Membership?.Value.Role)) >= Roles.Rank(minimum)
            ? AuthStore.Require(AuthStore.MembershipCollection, Instance, MembershipKey(access.Project.Value.OrganizationId, access.Membership!.Value.UserId),
                Filter("in", RoleList(Roles.Organization.Where(role => Roles.Rank(Roles.Implied(role)) >= Roles.Rank(minimum)))), access.Membership.Version)
            : AuthStore.Require(AuthStore.ProjectMemberCollection, Instance, ProjectMemberKey(access.Project.Value.Id, access.Grant!.Value.UserId),
                Filter("in", RoleList(Roles.Project.Where(role => Roles.Rank(role) >= Roles.Rank(minimum)))), access.Grant.Version);

    internal async Task<T> CommitAsync<T>(Func<Task<Plan<T>>> plan, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            var next = await plan().ConfigureAwait(false);
            if (next.Operations.Count == 0 || await store.TransactAsync(next.Operations, cancellationToken).ConfigureAwait(false) is not { } failed)
            {
                return next.Result;
            }

            if (failed == next.Claim)
            {
                throw next.ClaimTaken ?? Conflict("name_taken", "The organization already has a project with this name.");
            }
        }

        throw ApiException.Unavailable();
    }

    /// <summary>
    /// One page of records matching <paramref name="conditions"/>, sorted inside TerraBase by <paramref name="sort"/>
    /// then id; <paramref name="after"/> is the cursor TerraBase answered, valid only for the same list.
    /// </summary>
    internal Task<Page<Versioned<T>>> PageAsync<T>(string collection, IReadOnlyList<JsonObject> conditions, JsonTypeInfo<T> type, string sort, int limit, string? after, CancellationToken cancellationToken)
        where T : class => this.PageAsync(collection, conditions, type, sort, limit, after, descending: false, cancellationToken);

    /// <summary>As the other overload, newest first when <paramref name="descending"/>.</summary>
    internal async Task<Page<Versioned<T>>> PageAsync<T>(string collection, IReadOnlyList<JsonObject> conditions, JsonTypeInfo<T> type, string sort, int limit, string? after, bool descending, CancellationToken cancellationToken)
        where T : class
    {
        TerraBaseDocumentPage page;
        try
        {
            page = await store.PageAsync(collection, Instance, conditions, [sort], limit, after, descending, cancellationToken).ConfigureAwait(false);
        }
        catch (TerraBaseException error) when (error.ErrorCode == "invalid_cursor")
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "invalid_request", "after is not a cursor of this list.");
        }

        var items = new List<Versioned<T>>(page.Items.Count);
        foreach (var document in page.Items)
        {
            if (document.Document.Deserialize(type) is { } value)
            {
                items.Add(new(value, document.Version));
            }
        }

        return new(items, page.NextCursor);
    }

    internal Task<Versioned<OrganizationRecord>?> OrganizationAsync(string organizationId, CancellationToken cancellationToken) =>
        this.GetAsync(AuthStore.OrganizationCollection, organizationId, PlatformJson.Default.OrganizationRecord,
            organization => organization.InstanceId == Instance && organization.Id == organizationId, cancellationToken);

    /// <summary>The user's organization role, or null when it is no member (a collaborator has none either).</summary>
    internal async Task<string?> RoleAsync(string organizationId, string userId, CancellationToken cancellationToken) =>
        (await this.MembershipAsync(organizationId, userId, cancellationToken).ConfigureAwait(false))?.Value.Role;

    internal Task<Versioned<ProjectRecord>?> ProjectAsync(string projectId, CancellationToken cancellationToken) =>
        this.GetAsync(AuthStore.ProjectCollection, projectId, PlatformJson.Default.ProjectRecord,
            project => project.InstanceId == Instance && project.Id == projectId, cancellationToken);

    private static string MembershipKey(string organizationId, string userId) => $"{organizationId}/{userId}";

    private static string ProjectMemberKey(string projectId, string userId) => $"{projectId}/{userId}";

    // Case-insensitive as an ordinal ignore-case comparison is: by invariant uppercase, so 'σ' and 'ς' are one name.
    private static string ProjectNameKey(string organizationId, string name) => $"{organizationId}/{name.ToUpperInvariant()}";

    private static JsonElement Serialize<T>(T value, JsonTypeInfo<T> type) => JsonSerializer.SerializeToElement(value, type);

    private static JsonArray RoleList(IEnumerable<string> roles) => new("/role", new JsonArray([.. roles.Select(role => (JsonNode?)JsonValue.Create(role))]));

    private static JsonObject RoleIn(IEnumerable<string> roles) => new() { ["in"] = RoleList(roles) };

    // The organization without its pending transfer when giving userId the organization role (null: removed) takes the
    // starter out of the owners or the nominee out of the members who may confirm, so the offer does not come back with
    // the role.
    private static OrganizationRecord Unoffered(OrganizationRecord organization, string userId, string? role) =>
        organization.Transfer is { } transfer
            && (transfer.FromUserId == userId && role != Roles.Owner || transfer.ToUserId == userId && role is null or Roles.Owner)
                ? organization with { Transfer = null }
                : organization;

    // Rewrites a membership unchanged at the version read: it conflicts with any other change to it.
    private static StoreOperation Touch(Versioned<MembershipRecord> membership) =>
        AuthStore.Replace(AuthStore.MembershipCollection, Instance, MembershipKey(membership.Value.OrganizationId, membership.Value.UserId),
            Serialize(membership.Value, PlatformJson.Default.MembershipRecord), membership.Version);

    // The pending transfer while it still holds: unexpired, its starter still an owner and its nominee a non-owner member.
    private async Task<(OwnershipTransferRecord Transfer, Versioned<MembershipRecord> Starter)?> LiveTransferAsync(Versioned<OrganizationRecord> organization, CancellationToken cancellationToken)
    {
        if (organization.Value.Transfer is not { } transfer || transfer.ExpiresAt <= clock.GetUtcNow()
            || await this.MembershipAsync(organization.Value.Id, transfer.FromUserId, cancellationToken).ConfigureAwait(false) is not { Value.Role: Roles.Owner } starter
            || await this.MembershipAsync(organization.Value.Id, transfer.ToUserId, cancellationToken).ConfigureAwait(false) is not { Value.Role: Roles.Viewer or Roles.Member or Roles.Admin })
        {
            return null;
        }

        return (transfer, starter);
    }

    // Whether the user has a role on a live project of the organization other than except; a role left on a deleted
    // project (its clean-up failed) does not count.
    private async Task<bool> CollaboratesAsync(string userId, string organizationId, string? except, CancellationToken cancellationToken)
    {
        await foreach (var grant in this.GrantsAsync(userId, organizationId, cancellationToken).ConfigureAwait(false))
        {
            if (grant.Value.ProjectId != except && await this.ProjectAsync(grant.Value.ProjectId, cancellationToken).ConfigureAwait(false) is { } project && project.Value.OrganizationId == organizationId)
            {
                return true;
            }
        }

        return false;
    }

    // Accounts are never deleted today; a missing one is left out rather than shown without an email.
    private Task<IReadOnlyList<(T Item, string Email)>> WithEmailsAsync<T>(IReadOnlyList<T> items, Func<T, string> userId, CancellationToken cancellationToken) =>
        ReadEachAsync(items, async (item, token) =>
            await users.GetAsync(Instance, userId(item), token).ConfigureAwait(false) is { } user ? ((T, string)?)(item, user.Email) : null, cancellationToken);

    /// <summary>
    /// Runs <paramref name="read"/> for every item, at most eight at a time, and keeps the non-null answers in the items'
    /// order, so a page costs a few round trips rather than one per item.
    /// </summary>
    private static async Task<IReadOnlyList<TResult>> ReadEachAsync<TItem, TResult>(IReadOnlyList<TItem> items, Func<TItem, CancellationToken, Task<TResult?>> read, CancellationToken cancellationToken)
        where TResult : struct
    {
        var results = new TResult?[items.Count];
        await Parallel.ForAsync(0, items.Count, new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = cancellationToken },
            async (index, token) => results[index] = await read(items[index], token).ConfigureAwait(false)).ConfigureAwait(false);
        return [.. results.Where(result => result.HasValue).Select(result => result!.Value)];
    }

    // Deletes the matching invitations, each in one transaction with the token and address claims it still holds, so no
    // claim outlives its invitation.
    private async Task RetireInvitationsAsync(IReadOnlyList<(string, string)> equals, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var found in this.QueryAsync(AuthStore.InvitationCollection, equals, PlatformJson.Default.InvitationRecord, cancellationToken).ConfigureAwait(false))
            {
                await this.CommitAsync(async () =>
                {
                    if (await this.GetAsync(AuthStore.InvitationCollection, found.Value.Id, PlatformJson.Default.InvitationRecord,
                        invitation => invitation.InstanceId == Instance && invitation.Id == found.Value.Id, cancellationToken).ConfigureAwait(false) is not { } invitation)
                    {
                        return new Plan<bool>([], true);
                    }

                    List<StoreOperation> operations = [AuthStore.Delete(AuthStore.InvitationCollection, Instance, invitation.Value.Id, invitation.Version)];
                    foreach (var (kind, key) in Invitations.Claims(invitation.Value))
                    {
                        if (await store.FindUniqueAsync(Instance, kind, key, cancellationToken).ConfigureAwait(false) is { } held && held.OwnerId == invitation.Value.Id)
                        {
                            operations.Add(AuthStore.Release(Instance, kind, key, held.Version));
                        }
                    }

                    return new Plan<bool>(operations, true);
                }, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception error) when (error is HttpRequestException or TimeoutException or ApiException)
        {
            logger.ZLogWarning($"Cleaning up {AuthStore.InvitationCollection:@Collection} after a deletion failed ({error.GetType().Name:@Error}); what is left grants nothing");
        }
    }

    private async Task CleanUpAsync<T>(string collection, IReadOnlyList<(string, string)> equals, JsonTypeInfo<T> type, Func<T, string> key, CancellationToken cancellationToken)
        where T : class
    {
        try
        {
            await foreach (var record in this.QueryAsync(collection, equals, type, cancellationToken).ConfigureAwait(false))
            {
                await store.CompareAndDeleteAsync(collection, Instance, key(record.Value), record.Version, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception error) when (error is HttpRequestException or TimeoutException or ApiException)
        {
            logger.ZLogWarning($"Cleaning up {collection:@Collection} after a deletion failed ({error.GetType().Name:@Error}); what is left grants nothing");
        }
    }

    private async IAsyncEnumerable<Versioned<T>> QueryAsync<T>(string collection, IReadOnlyList<(string, string)> equals, JsonTypeInfo<T> type, [EnumeratorCancellation] CancellationToken cancellationToken)
        where T : class
    {
        await foreach (var document in store.QueryAsync(collection, Instance, equals, cancellationToken).ConfigureAwait(false))
        {
            if (document.Document.Deserialize(type) is { } value)
            {
                yield return new(value, document.Version);
            }
        }
    }

    // A user's explicit roles in one organization: at most one per project.
    private IAsyncEnumerable<Versioned<ProjectMemberRecord>> GrantsAsync(string userId, string organizationId, CancellationToken cancellationToken) =>
        this.QueryAsync(AuthStore.ProjectMemberCollection, [("/userId", userId), ("/organizationId", organizationId)], PlatformJson.Default.ProjectMemberRecord, cancellationToken)
            .Where(grant => grant.Value.UserId == userId && grant.Value.OrganizationId == organizationId);

    private async Task<Versioned<T>?> GetAsync<T>(string collection, string key, JsonTypeInfo<T> type, Func<T, bool> belongs, CancellationToken cancellationToken)
        where T : class
    {
        var document = await store.GetAsync(collection, Instance, key, cancellationToken).ConfigureAwait(false);
        return document?.Document.Deserialize(type) is { } value && belongs(value) ? new(value, document.Version) : null;
    }

    private Task<Versioned<MembershipRecord>?> MembershipAsync(string organizationId, string userId, CancellationToken cancellationToken) =>
        this.GetAsync(AuthStore.MembershipCollection, MembershipKey(organizationId, userId), PlatformJson.Default.MembershipRecord,
            membership => membership.InstanceId == Instance && membership.OrganizationId == organizationId && membership.UserId == userId, cancellationToken);

    private Task<Versioned<ProjectMemberRecord>?> ProjectMemberAsync(string projectId, string userId, CancellationToken cancellationToken) =>
        this.GetAsync(AuthStore.ProjectMemberCollection, ProjectMemberKey(projectId, userId), PlatformJson.Default.ProjectMemberRecord,
            grant => grant.InstanceId == Instance && grant.ProjectId == projectId && grant.UserId == userId, cancellationToken);

    /// <summary>
    /// The operations of one change and what it answers. A failed <paramref name="Claim"/> answers
    /// <paramref name="ClaimTaken"/>, by default a taken project name.
    /// </summary>
    internal sealed record Plan<T>(IReadOnlyList<StoreOperation> Operations, T Result, int? Claim = null, ApiException? ClaimTaken = null);
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(OrganizationRecord))]
[JsonSerializable(typeof(MembershipRecord))]
[JsonSerializable(typeof(ProjectRecord))]
[JsonSerializable(typeof(ProjectMemberRecord))]
[JsonSerializable(typeof(ApiKeyRecord))]
[JsonSerializable(typeof(ApiKeyUsageRecord))]
[JsonSerializable(typeof(InvitationRecord))]
[JsonSerializable(typeof(AuditEventRecord))]
internal sealed partial class PlatformJson : JsonSerializerContext;

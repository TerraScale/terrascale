using System.Collections.Concurrent;
using System.Diagnostics;
using TerraBase.Engine;
using TerraBase.Errors;
using TerraBase.Features.UserAuth;
using TerraBase.Hosting;
using TerraBase.Tenancy;

namespace TerraBase.Features.Management;

internal sealed record ProjectCatalogBinding(
    int Version, string Issuer, string OrganizationId, string? AuthorityOrganizationId, string VerifiedBy, DateTimeOffset VerifiedAt);

internal sealed partial class EngineManagementService : IProjectCatalog
{
    // Every project-token request resolves its project here (twice: admission and authorization), so the read is reused while
    // the catalog has not committed since it began, like the database lookups.
    private readonly ConcurrentDictionary<(string Project, string, string), (long Sequence, ProjectMapping Mapping)> _projectMappings = new();

    public async ValueTask<ResolveProjectResult> ResolveProjectAsync(string projectId, string actor, bool create, CancellationToken cancellationToken)
    {
        if (options.AuthIssuer is not { } issuer)
        {
            return ManagementErrors.NotFound;
        }

        var sequence = (await engine.OpenSystemDatabaseAsync(cancellationToken).ConfigureAwait(false)).CommitSequence;
        var key = (projectId, "", "");
        var cacheable = CommitTransaction.Value is null;
        ProjectMapping mapping;
        if (cacheable && _projectMappings.TryGetValue(key, out var cached) && cached.Sequence == sequence)
        {
            mapping = cached.Mapping;
        }
        else
        {
            var lookup = await ShareMissAsync("project", key, sequence, token => ReadAsync<ProjectMappingResult>(async transaction =>
            {
                var organization = await GetAsync(transaction, ProjectOrganizations, projectId, Id, token).ConfigureAwait(false);
                return new ProjectMapping(organization, organization is not null
                    && await IsProjectBoundAsync(transaction, projectId, organization, token).ConfigureAwait(false));
            }, static error => error, token), cancellationToken).ConfigureAwait(false);
            if (lookup is not ProjectMapping read)
            {
                return lookup switch
                {
                    Unavailable error => error,
                    ProjectMapping => throw new UnreachableException(),
                };
            }

            mapping = read;
            if (cacheable)
            {
                Cache(_projectMappings, key, sequence, mapping);
            }
        }

        if (mapping.Organization is not null || !create)
        {
            return mapping is { Verified: true, Organization: not null } ? mapping.Organization : ManagementErrors.NotFound;
        }

        // Keep layout 1 and catalog 5: these records are storage envelopes, not ts-auth identity or membership.
        return await WriteAsync<ResolveProjectResult>(async transaction =>
        {
            if (await GetAsync(transaction, ProjectOrganizations, projectId, Id, cancellationToken).ConfigureAwait(false) is { } existing)
            {
                return await IsProjectBoundAsync(transaction, projectId, existing, cancellationToken).ConfigureAwait(false) ? existing : ManagementErrors.NotFound;
            }

            if (await transaction.GetVersionAsync(ProjectOrganizations, projectId, cancellationToken).ConfigureAwait(false) is not null)
            {
                return ManagementErrors.NotFound;
            }

            // Legacy commits let users choose organization ULIDs; equality is not authority over a signed project.
            var namespaceId = NewId();
            if (await transaction.GetVersionAsync(Organizations, namespaceId, cancellationToken).ConfigureAwait(false) is not null)
            {
                return ManagementErrors.NotFound;
            }

            var owner = new Organization(namespaceId, projectId, actor, time.GetUtcNow());
            var project = new Project(projectId, owner.Id, projectId, time.GetUtcNow());
            await PutAsync(transaction, Organizations, owner.Id, owner, TerraBaseJsonContext.Default.Organization, cancellationToken).ConfigureAwait(false);
            await PutAsync(transaction, Projects(owner.Id), project.Id, project, TerraBaseJsonContext.Default.Project, cancellationToken).ConfigureAwait(false);
            await PutAsync(transaction, ProjectOrganizations, project.Id, owner.Id, Id, cancellationToken).ConfigureAwait(false);
            await PutAsync(transaction, Bindings(project.Id), "ts_auth",
                new ProjectCatalogBinding(1, issuer, owner.Id, null, actor, time.GetUtcNow()), TerraBaseJsonContext.Default.ProjectCatalogBinding, cancellationToken)
                .ConfigureAwait(false);
            return owner.Id;
        }, static error => error, cancellationToken).ConfigureAwait(false);
    }

    public ValueTask<ResolveProjectResult> ProjectOrganizationAsync(string projectId, CancellationToken cancellationToken) =>
        ReadAsync<ResolveProjectResult>(async transaction =>
            await GetAsync(transaction, ProjectOrganizations, projectId, Id, cancellationToken).ConfigureAwait(false) is { } organization
                ? organization
                : ManagementErrors.NotFound, static error => error, cancellationToken);

    public async ValueTask<SetPlanResult> SetEnrolledPlanAsync(string projectId, PlanTier plan, CancellationToken cancellationToken) =>
        await WriteAsync<SetPlanResult>(async transaction =>
        {
            // Enrollment writes a binding without an authority organization and an organization without members, so no owner
            // can set this plan; a legacy binding names the organization whose owners do.
            if (await GetAsync(transaction, ProjectOrganizations, projectId, Id, cancellationToken).ConfigureAwait(false) is not { } organizationId
                || !await IsProjectBoundAsync(transaction, projectId, organizationId, cancellationToken).ConfigureAwait(false)
                || await GetAsync(transaction, Bindings(projectId), "ts_auth", TerraBaseJsonContext.Default.ProjectCatalogBinding, cancellationToken)
                    .ConfigureAwait(false) is not { AuthorityOrganizationId: null }
                || await GetAsync(transaction, Organizations, organizationId, TerraBaseJsonContext.Default.Organization, cancellationToken)
                    .ConfigureAwait(false) is not { } organization)
            {
                return ManagementErrors.NotFound;
            }

            organization = organization with { Plan = plan };
            await PutAsync(transaction, Organizations, organization.Id, organization, TerraBaseJsonContext.Default.Organization, cancellationToken)
                .ConfigureAwait(false);
            await RecordAsync(transaction, organizationId, OperatorHandler.KeyId, "settings.plan_updated", projectId, PlanName(plan), cancellationToken)
                .ConfigureAwait(false);
            return organization;
        }, static error => error, cancellationToken).ConfigureAwait(false);

    public async ValueTask<VerifyProjectBindingResult> VerifyBindingAsync(
        string userId, string organizationId, string projectId, string authorityOrganizationId, string token, CancellationToken cancellationToken)
    {
        if (options.AuthIssuer is not { } issuer)
        {
            return ManagementErrors.NotFound;
        }

        var precheck = await ReadAsync<VerifyProjectBindingResult>(async transaction =>
            await CanBindProjectAsync(transaction, userId, organizationId, projectId, cancellationToken).ConfigureAwait(false)
                ? new Done() : ManagementErrors.NotFound, static error => error, cancellationToken).ConfigureAwait(false);
        if (precheck is not Done)
        {
            return precheck;
        }

        VerifyProjectBindingResult authority = await projectAuthority.IsProjectAdministratorAsync(token, authorityOrganizationId, projectId, cancellationToken)
            .ConfigureAwait(false) switch
        {
            true => new Done(),
            false => ManagementErrors.NotFound,
            Unavailable error => error,
        };
        if (authority is not Done)
        {
            return authority;
        }

        // The online authority check holds no storage locks. Recheck storage ownership and mapping in the committing transaction.
        return await WriteAsync<VerifyProjectBindingResult>(async transaction =>
        {
            if (!await CanBindProjectAsync(transaction, userId, organizationId, projectId, cancellationToken).ConfigureAwait(false))
            {
                return new NotFound();
            }

            if (await GetAsync(transaction, Bindings(projectId), "ts_auth", TerraBaseJsonContext.Default.ProjectCatalogBinding, cancellationToken)
                .ConfigureAwait(false) is { } existing)
            {
                return existing is { Version: 1 } && existing.Issuer == issuer && existing.OrganizationId == organizationId
                    && existing.AuthorityOrganizationId == authorityOrganizationId
                    ? new Done() : new Conflict("the project already has a different verified binding", "binding_conflict");
            }

            await PutAsync(transaction, Bindings(projectId), "ts_auth",
                new ProjectCatalogBinding(1, issuer, organizationId, authorityOrganizationId, userId, time.GetUtcNow()),
                TerraBaseJsonContext.Default.ProjectCatalogBinding, cancellationToken).ConfigureAwait(false);
            await RecordAsync(transaction, organizationId, userId, "project_binding_verified", projectId, detail: null, cancellationToken).ConfigureAwait(false);
            return new Done();
        }, static error => error, cancellationToken).ConfigureAwait(false);
    }

    private sealed record ProjectMapping(string? Organization, bool Verified);

    private readonly union ProjectMappingResult(ProjectMapping, Unavailable);

    private static string Bindings(string projectId) => SystemCatalog.ProjectState(projectId) + "binding";

    private async ValueTask<bool> IsProjectBoundAsync(IStorageTransaction transaction, string projectId, string organizationId, CancellationToken cancellationToken) =>
        await GetAsync(transaction, Bindings(projectId), "ts_auth", TerraBaseJsonContext.Default.ProjectCatalogBinding, cancellationToken).ConfigureAwait(false)
            is { Version: 1 } binding && binding.Issuer == options.AuthIssuer && binding.OrganizationId == organizationId
        && await GetAsync(transaction, Projects(organizationId), projectId, TerraBaseJsonContext.Default.Project, cancellationToken).ConfigureAwait(false)
            is { } project && project.OrganizationId == organizationId;

    private static async ValueTask<bool> CanBindProjectAsync(
        IStorageTransaction transaction, string userId, string organizationId, string projectId, CancellationToken cancellationToken) =>
        await AuthorizeMemberAsync(transaction, userId, organizationId, OrganizationRole.Owner, cancellationToken).ConfigureAwait(false) is OrganizationMember
        && await GetAsync(transaction, Projects(organizationId), projectId, TerraBaseJsonContext.Default.Project, cancellationToken).ConfigureAwait(false) is not null
        && await GetAsync(transaction, ProjectOrganizations, projectId, Id, cancellationToken).ConfigureAwait(false) == organizationId;

    private static bool ProjectActor(string userId, string organizationId) =>
        ProjectAccess.Current is { } access && access.Actor == userId && access.OrganizationId == organizationId;
}

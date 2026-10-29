using System.Collections.Concurrent;
using System.Diagnostics;
using TerraBase.Engine;
using TerraBase.Errors;
using TerraBase.Features.UserAuth;
using TerraBase.Features.Platform;
using TerraBase.Configuration;
using TerraBase.Hosting;
using TerraBase.Tenancy;

namespace TerraBase.Features.Management;

internal sealed partial class EngineManagementService : IProjectCatalog
{
    // Every project-token request resolves its project here (twice: admission and authorization), so the read is reused while
    // the catalog has not committed since it began, like the database lookups.
    private readonly ConcurrentDictionary<(string Project, string, string), (long Sequence, ProjectMapping Mapping)> _projectMappings = new();

    public async ValueTask<ResolveProjectResult> ResolveProjectAsync(string projectId, string actor, string? createIn, CancellationToken cancellationToken)
    {
        if (options.AuthIssuer is null)
        {
            return ManagementErrors.NotFound;
        }

        var sequence = (await engine.OpenSystemDatabaseAsync(cancellationToken).ConfigureAwait(false)).CommitSequence;
        var key = (projectId, "", "");
        ProjectMapping mapping;
        if (_projectMappings.TryGetValue(key, out var cached) && cached.Sequence == sequence)
        {
            mapping = cached.Mapping;
        }
        else
        {
            var lookup = await ShareMissAsync("project", key, sequence, token => ReadAsync<ProjectMappingResult>(async transaction =>
            {
                var organization = await GetAsync(transaction, ProjectOrganizations, projectId, Id, token).ConfigureAwait(false);
                return new ProjectMapping(organization, organization is not null
                    && await IsProjectRegisteredAsync(transaction, projectId, organization, token).ConfigureAwait(false));
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
            Cache(_projectMappings, key, sequence, mapping);
        }

        if (mapping.Organization is not null || createIn is not { } organizationId)
        {
            return mapping is { Registered: true, Organization: not null } && (createIn is null || createIn == mapping.Organization) ? mapping.Organization : ManagementErrors.NotFound;
        }

        return await WriteAsync<ResolveProjectResult>(async transaction =>
        {
            if (await GetAsync(transaction, ProjectOrganizations, projectId, Id, cancellationToken).ConfigureAwait(false) is { } existing)
            {
                return await IsProjectRegisteredAsync(transaction, projectId, existing, cancellationToken).ConfigureAwait(false) && existing == organizationId ? existing : ManagementErrors.NotFound;
            }

            if (await transaction.GetVersionAsync(ProjectOrganizations, projectId, cancellationToken).ConfigureAwait(false) is not null
                || await IsProjectClosedAsync(transaction, projectId, cancellationToken).ConfigureAwait(false))
            {
                return ManagementErrors.NotFound;
            }

            var owner = await GetAsync(transaction, Organizations, organizationId, TerraBaseJsonContext.Default.Organization, cancellationToken).ConfigureAwait(false);
            if (owner is null)
            {
                owner = new Organization(organizationId, organizationId, OperatorHandler.KeyId, time.GetUtcNow());
                await PutAsync(transaction, Organizations, owner.Id, owner, TerraBaseJsonContext.Default.Organization, cancellationToken).ConfigureAwait(false);
            }
            else if (owner.CreatedBy != OperatorHandler.KeyId)
            {
                return ManagementErrors.NotFound;
            }

            var project = new Project(projectId, owner.Id, projectId, time.GetUtcNow());
            await PutAsync(transaction, Projects(owner.Id), project.Id, project, TerraBaseJsonContext.Default.Project, cancellationToken).ConfigureAwait(false);
            await PutAsync(transaction, ProjectOrganizations, project.Id, owner.Id, Id, cancellationToken).ConfigureAwait(false);
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
            if (await GetAsync(transaction, ProjectOrganizations, projectId, Id, cancellationToken).ConfigureAwait(false) is not { } organizationId
                || !await IsProjectRegisteredAsync(transaction, projectId, organizationId, cancellationToken).ConfigureAwait(false)
                || await GetAsync(transaction, Organizations, organizationId, TerraBaseJsonContext.Default.Organization, cancellationToken)
                    .ConfigureAwait(false) is not { CreatedBy: OperatorHandler.KeyId } organization)
            {
                return ManagementErrors.NotFound;
            }

            if (organization.Plan == plan)
            {
                return organization;
            }

            organization = organization with { Plan = plan };
            await PutAsync(transaction, Organizations, organization.Id, organization, TerraBaseJsonContext.Default.Organization, cancellationToken)
                .ConfigureAwait(false);

            return organization;
        }, static error => error, cancellationToken).ConfigureAwait(false);

    public async ValueTask<SetPlanResult> SetStoragePlanAsync(string organizationId, PlanTier plan, CancellationToken cancellationToken) =>
        await WriteAsync<SetPlanResult>(async transaction =>
        {
            // A deleted organization stays deleted; Hobby is the default, so a missing organization needs no record for it.
            var version = await transaction.GetVersionAsync(Organizations, organizationId, cancellationToken).ConfigureAwait(false);
            var organization = version is { Deleted: false }
                ? await GetAsync(transaction, Organizations, organizationId, TerraBaseJsonContext.Default.Organization, cancellationToken).ConfigureAwait(false)
                : null;
            if (version is { Deleted: true } || organization is null && plan == PlanTier.Hobby
                || organization is not null && organization.CreatedBy != OperatorHandler.KeyId)
            {
                return ManagementErrors.NotFound;
            }

            if (organization?.Plan == plan)
            {
                return organization;
            }

            // Created as enrollment creates it (ResolveProjectAsync), which then adopts it with this plan.
            organization = (organization ?? new Organization(organizationId, organizationId, OperatorHandler.KeyId, time.GetUtcNow())) with { Plan = plan };
            await PutAsync(transaction, Organizations, organization.Id, organization, TerraBaseJsonContext.Default.Organization, cancellationToken)
                .ConfigureAwait(false);

            return organization;
        }, static error => error, cancellationToken).ConfigureAwait(false);

    private sealed record ProjectMapping(string? Organization, bool Registered);

    private readonly union ProjectMappingResult(ProjectMapping, Unavailable);

    private async ValueTask<bool> IsProjectRegisteredAsync(IStorageTransaction transaction, string projectId, string organizationId, CancellationToken cancellationToken) =>
        await GetAsync(transaction, Projects(organizationId), projectId, TerraBaseJsonContext.Default.Project, cancellationToken).ConfigureAwait(false)
            is { } project && project.OrganizationId == organizationId
            && (!PlatformDatabases.IsPlatform(organizationId) || options.AuthIssuer == TerraBaseOptions.PlatformAuthIssuer);

    private static bool ProjectActor(string userId, string organizationId) =>
        ProjectAccess.Current is { } access && access.Actor == userId && access.OrganizationId == organizationId;
}

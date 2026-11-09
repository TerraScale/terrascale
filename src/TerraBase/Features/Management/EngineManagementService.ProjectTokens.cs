using System.Collections.Concurrent;
using System.Diagnostics;
using TerraBase.Engine;
using TerraBase.Errors;
using TerraBase.Features.UserAuth;
using TerraBase.Features.Platform;
using TerraBase.Configuration;
using TerraBase.Tenancy;

namespace TerraBase.Features.Management;

internal sealed partial class EngineManagementService : IProjectCatalog
{
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
                var organization = await CatalogTables.ProjectOrganizationAsync(transaction, projectId, token).ConfigureAwait(false);
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
            if (await CatalogTables.ProjectOrganizationAsync(transaction, projectId, cancellationToken).ConfigureAwait(false) is { } existing)
            {
                return await IsProjectRegisteredAsync(transaction, projectId, existing, cancellationToken).ConfigureAwait(false) && existing == organizationId ? existing : ManagementErrors.NotFound;
            }

            if (await CatalogTables.RetiredAsync(transaction, projectId, cancellationToken).ConfigureAwait(false)
                || await IsProjectClosedAsync(transaction, projectId, cancellationToken).ConfigureAwait(false))
            {
                return ManagementErrors.NotFound;
            }

            var owner = await CatalogTables.OrganizationAsync(transaction, organizationId, cancellationToken).ConfigureAwait(false);
            if (owner is null)
            {
                if (await CatalogTables.RetiredAsync(transaction, organizationId, cancellationToken).ConfigureAwait(false))
                {
                    return ManagementErrors.NotFound;
                }

                owner = new Organization(organizationId, organizationId, OperatorHandler.KeyId, time.GetUtcNow());
                _ = await CatalogTables.PutOrganizationAsync(transaction, owner, cancellationToken).ConfigureAwait(false);
            }
            else if (owner.CreatedBy != OperatorHandler.KeyId)
            {
                return ManagementErrors.NotFound;
            }

            var project = new Project(projectId, owner.Id, projectId, time.GetUtcNow());
            _ = await CatalogTables.PutProjectAsync(transaction, project, cancellationToken).ConfigureAwait(false);
            return owner.Id;
        }, static error => error, cancellationToken).ConfigureAwait(false);
    }

    public ValueTask<ResolveProjectResult> ProjectOrganizationAsync(string projectId, CancellationToken cancellationToken) =>
        ReadAsync<ResolveProjectResult>(async transaction =>
            await CatalogTables.ProjectOrganizationAsync(transaction, projectId, cancellationToken).ConfigureAwait(false) is { } organization
                ? organization
                : ManagementErrors.NotFound, static error => error, cancellationToken);

    public async ValueTask<SetPlanResult> SetEnrolledPlanAsync(string projectId, PlanTier plan, CancellationToken cancellationToken) =>
        await WriteAsync<SetPlanResult>(async transaction =>
        {
            if (await CatalogTables.ProjectOrganizationAsync(transaction, projectId, cancellationToken).ConfigureAwait(false) is not { } organizationId
                || !await IsProjectRegisteredAsync(transaction, projectId, organizationId, cancellationToken).ConfigureAwait(false)
                || await CatalogTables.OrganizationAsync(transaction, organizationId, cancellationToken).ConfigureAwait(false)
                    is not { CreatedBy: OperatorHandler.KeyId } organization)
            {
                return ManagementErrors.NotFound;
            }

            if (organization.Plan == plan && await IsPlanPublishedAsync(transaction, organizationId, plan, cancellationToken).ConfigureAwait(false))
            {
                return organization;
            }

            organization = organization with { Plan = plan };
            _ = await CatalogTables.PutOrganizationAsync(transaction, organization, cancellationToken).ConfigureAwait(false);
            await PublishPlanAsync(transaction, organizationId, plan, cancellationToken).ConfigureAwait(false);
            return organization;
        }, static error => error, cancellationToken).ConfigureAwait(false);

    public async ValueTask<SetPlanResult> SetStoragePlanAsync(string organizationId, PlanTier plan, CancellationToken cancellationToken) =>
        await WriteAsync<SetPlanResult>(async transaction =>
        {
            var organization = await CatalogTables.OrganizationAsync(transaction, organizationId, cancellationToken).ConfigureAwait(false);
            if (await CatalogTables.RetiredAsync(transaction, organizationId, cancellationToken).ConfigureAwait(false)
                || organization is not null && organization.CreatedBy != OperatorHandler.KeyId)
            {
                return ManagementErrors.NotFound;
            }

            if (organization?.Plan == plan && await IsPlanPublishedAsync(transaction, organizationId, plan, cancellationToken).ConfigureAwait(false))
            {
                return organization;
            }

            organization = (organization ?? new Organization(organizationId, organizationId, OperatorHandler.KeyId, time.GetUtcNow())) with { Plan = plan };
            _ = await CatalogTables.PutOrganizationAsync(transaction, organization, cancellationToken).ConfigureAwait(false);
            await PublishPlanAsync(transaction, organizationId, plan, cancellationToken).ConfigureAwait(false);

            return organization;
        }, static error => error, cancellationToken).ConfigureAwait(false);

    private sealed record ProjectMapping(string? Organization, bool Registered);

    private readonly union ProjectMappingResult(ProjectMapping, Unavailable);

    private async ValueTask<bool> IsProjectRegisteredAsync(IStorageTransaction transaction, string projectId, string organizationId, CancellationToken cancellationToken) =>
        await CatalogTables.OrganizationProjectAsync(transaction, organizationId, projectId, cancellationToken).ConfigureAwait(false) is not null
            && (!PlatformDatabases.IsPlatform(organizationId) || options.AuthIssuer == TerraBaseOptions.PlatformAuthIssuer);

    private static bool ProjectActor(string userId, string organizationId) =>
        ProjectAccess.Current is { } access && access.Actor == userId && access.OrganizationId == organizationId;
}

using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using TerraBase.Configuration;
using TerraBase.Engine;
using TerraBase.Errors;
using TerraBase.Features.ApiKeys;
using TerraBase.Features.Backup;
using TerraBase.Features.Management.Backups;
using TerraBase.Features.Platform;
using TerraBase.Features.Qos;
using TerraBase.Features.Regions;
using TerraBase.Features.Catalog;
using TerraBase.Features.UserAuth;
using TerraBase.Hosting;
using TerraBase.Primitives;
using TerraBase.Tenancy;

namespace TerraBase.Features.Management;

internal sealed partial class EngineManagementService
{

    public ValueTask<CreateProjectResult> CreateProjectAsync(
        string userId, string organizationId, string name, CancellationToken cancellationToken) =>
        CreateProjectAsync(userId, organizationId, NewId(), name, cancellationToken);

    private async ValueTask<CreateProjectResult> CreateProjectAsync(
        string userId, string organizationId, string id, string name, CancellationToken cancellationToken)
    {
        var project = new Project(id, organizationId, name, time.GetUtcNow());
        return await WriteAsync<CreateProjectResult>(async transaction =>
        {
            var access = await WritableAsync(transaction, userId, organizationId, cancellationToken).ConfigureAwait(false);
            if (access is not Organization)
            {
                return access switch
                {
                    NotFound error => error,
                    Forbidden error => error,
                    Organization => throw new UnreachableException(),
                };
            }

            if (await CatalogTables.ProjectAsync(transaction, id, cancellationToken).ConfigureAwait(false) is { } known)
            {
                return known.OrganizationId == organizationId && known.Name == name ? known : ManagementErrors.IdTaken;
            }

            if (await CatalogTables.RetiredAsync(transaction, id, cancellationToken).ConfigureAwait(false))
            {
                return ManagementErrors.IdTaken;
            }

            if (await CatalogTables.ProjectNamedAsync(transaction, organizationId, Key(name), cancellationToken).ConfigureAwait(false) is not null)
            {
                return ManagementErrors.NameTaken;
            }

            _ = await CatalogTables.PutProjectAsync(transaction, project, cancellationToken).ConfigureAwait(false);
            return project;
        }, static error => error, cancellationToken).ConfigureAwait(false);
    }

    public ValueTask<ListProjectsResult> ListProjectsAsync(
        string userId, string organizationId, string? after, int limit, CancellationToken cancellationToken) =>
        ReadAsync<ListProjectsResult>(async transaction =>
            await GetOrganizationAsync(transaction, userId, organizationId, cancellationToken).ConfigureAwait(false) is null
                ? ManagementErrors.NotFound
                : await CatalogTables.ProjectsAsync(transaction, organizationId, after, limit, cancellationToken).ConfigureAwait(false),
            static error => error, cancellationToken);

    public ValueTask<GetProjectResult> GetProjectAsync(string userId, string organizationId, string projectId, CancellationToken cancellationToken) =>
        ReadAsync<GetProjectResult>(async transaction =>
            await GetProjectAsync(transaction, userId, organizationId, projectId, cancellationToken).ConfigureAwait(false) is { } project
                ? project
                : ManagementErrors.NotFound,
            static error => error, cancellationToken);

    public async ValueTask<DeleteProjectResult> DeleteProjectAsync(
        string userId, string organizationId, string projectId, CancellationToken cancellationToken) =>
        await WriteAsync<DeleteProjectResult>(async transaction =>
        {
            var access = await WritableAsync(transaction, userId, organizationId, cancellationToken).ConfigureAwait(false);
            if (access is not Organization)
            {
                return access switch
                {
                    NotFound error => error,
                    Forbidden error => error,
                    Organization => throw new UnreachableException(),
                };
            }

            if (await GetProjectAsync(transaction, userId, organizationId, projectId, cancellationToken).ConfigureAwait(false) is not { } project)
            {
                return ManagementErrors.NotFound;
            }

            if (await CatalogTables.HasDatabasesAsync(transaction, project.Id, cancellationToken).ConfigureAwait(false)
                || await AnyAsync(transaction, EngineApiKeyStore.ProjectKeys(project.Id), cancellationToken).ConfigureAwait(false)
                || await AnyAsync(transaction, ProjectBackupDestinations.Kind(project.Id), cancellationToken).ConfigureAwait(false)
                || await AnyAsync(transaction, Features.FileStorage.FileStorageUsage.Buckets(project.Id), cancellationToken).ConfigureAwait(false))
            {
                return ManagementErrors.NotEmpty;
            }

            while (!await EraseRetainedDatabasesAsync(transaction, project.Id, cancellationToken).ConfigureAwait(false))
            {
            }
            await CatalogTables.DeleteProjectAsync(transaction, project.Id, cancellationToken).ConfigureAwait(false);
            await transaction.DeleteKindsAsync(SystemCatalog.ProjectState(project.Id), cancellationToken).ConfigureAwait(false);
            return new Done();
        }, static error => error, cancellationToken).ConfigureAwait(false);
}

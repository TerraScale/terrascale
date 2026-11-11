using System.Diagnostics;
using TerraBase.Errors;
using TerraBase.Features.Platform;
using TerraBase.Tenancy;

namespace TerraBase.Features.Management;

internal sealed partial class EngineManagementService
{
    public async ValueTask<SetDatabaseRegionsResult> RenameDatabaseAsync(
        string userId, string organizationId, string projectId, string databaseId, string name, CancellationToken cancellationToken) =>
        PlatformDatabases.IsPlatform(organizationId) ? ManagementErrors.PlatformDatabaseProtected : await WriteAsync<SetDatabaseRegionsResult>(async transaction =>
        {
            var project = await AuthorizedProjectAsync(transaction, userId, organizationId, projectId, cancellationToken).ConfigureAwait(false);
            if (project is not TenantContext tenant)
            {
                return project switch
                {
                    NotFound error => error,
                    Forbidden error => error,
                    TenantContext => throw new UnreachableException(),
                };
            }

            if (await GetDatabaseAsync(transaction, userId, organizationId, projectId, databaseId, cancellationToken).ConfigureAwait(false) is not { } database)
            {
                return ManagementErrors.NotFound;
            }

            if (database.Home(options.Region) != options.Region)
            {
                return ManagementErrors.WrongRegion;
            }

            if (database.Name == name)
            {
                return database;
            }

            if (await CatalogTables.DatabaseNamedAsync(transaction, projectId, name, cancellationToken).ConfigureAwait(false) is not null)
            {
                return ManagementErrors.NameTaken;
            }

            var renamed = database with { Name = name };
            await CatalogTables.PutDatabaseAsync(transaction, renamed, cancellationToken).ConfigureAwait(false);
            return renamed;
        }, static error => error, cancellationToken).ConfigureAwait(false);
}

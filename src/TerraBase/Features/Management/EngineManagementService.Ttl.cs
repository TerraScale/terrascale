using System.Diagnostics;
using TerraBase.Errors;
using TerraBase.Features.Platform;
using TerraBase.Features.UserAuth;
using TerraBase.Tenancy;
using ZLogger;

namespace TerraBase.Features.Management;

internal sealed partial class EngineManagementService
{
    private static readonly TimeSpan ExpirySweepInterval = TimeSpan.FromSeconds(5);
    private const int ExpiriesPerPass = 64;
    private DateTimeOffset _nextExpirySweep;

    public async ValueTask<SetDatabaseRegionsResult> SetDatabaseTtlAsync(
        string userId, string organizationId, string projectId, string databaseId, long? ttlSeconds, CancellationToken cancellationToken) =>
        PlatformDatabases.IsPlatform(organizationId) ? ManagementErrors.PlatformDatabaseProtected : await WriteAsync<SetDatabaseRegionsResult>(async transaction =>
        {
            var project = await AuthorizedProjectAsync(transaction, userId, organizationId, projectId, cancellationToken).ConfigureAwait(false);
            if (project is not TenantContext)
            {
                return project switch
                {
                    NotFound error => error,
                    Forbidden error => error,
                    TenantContext => throw new UnreachableException(),
                };
            }

            if (ProjectAccess.Current is { DatabaseName: not null } access && access.DatabaseId != databaseId
                || await CatalogTables.DatabaseAsync(transaction, projectId, databaseId, cancellationToken).ConfigureAwait(false) is not { } database)
            {
                return ManagementErrors.NotFound;
            }

            if (database.Home(options.Region) != options.Region)
            {
                return ManagementErrors.WrongRegion;
            }

            var changed = database with { ExpiresAt = DatabaseTtl.ExpiresAt(time.GetUtcNow(), ttlSeconds) };
            await CatalogTables.PutDatabaseAsync(transaction, changed, cancellationToken).ConfigureAwait(false);
            return changed;
        }, static error => error, cancellationToken).ConfigureAwait(false);

    private async ValueTask ExpireDatabasesAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        if (now < _nextExpirySweep)
        {
            return;
        }

        _nextExpirySweep = now + ExpirySweepInterval;
        var due = await ReadAsync<IReadOnlyList<TenantContext>?>(
            async transaction => await DatabaseTtl.DueAsync(transaction, now, ExpiriesPerPass, id => ExpiringHereAsync(transaction, id, cancellationToken), cancellationToken)
                .ConfigureAwait(false),
            static _ => null, cancellationToken).ConfigureAwait(false);
        foreach (var storage in due ?? [])
        {
            try
            {
                if (await DeleteRecordedAsync(storage, (transaction, token) => UnregisterExpiredAsync(transaction, storage, now, token), cancellationToken).ConfigureAwait(false)
                    is bool deleted && deleted)
                {
                    _ = Shredded(storage);
                    logger.ZLogInformation($"Deleted database {storage.DatabaseId:@Database} at the end of its TTL");
                }
            }
            catch (Exception error) when (!cancellationToken.IsCancellationRequested)
            {
                logger.ZLogWarning(error, $"Deleting expired database {storage.DatabaseId:@Database} failed; the next pass tries again");
            }
        }
    }

    private async ValueTask<TenantContext?> ExpiringHereAsync(Engine.IStorageTransaction transaction, string databaseId, CancellationToken cancellationToken)
    {
        if (await CatalogTables.DatabaseAsync(transaction, databaseId, cancellationToken).ConfigureAwait(false) is not { } database
            || database.Home(options.Region) != options.Region
            || await CatalogTables.ProjectOrganizationAsync(transaction, database.ProjectId, cancellationToken).ConfigureAwait(false) is not { } organization)
        {
            return null;
        }

        return new TenantContext(organization, await PlanUncheckedAsync(transaction, organization, cancellationToken).ConfigureAwait(false), database.ProjectId, database.Id);
    }

    private async ValueTask<bool> UnregisterExpiredAsync(Engine.IStorageTransaction transaction, TenantContext storage, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (await CatalogTables.DatabaseAsync(transaction, storage.ProjectId!, storage.DatabaseId!, cancellationToken).ConfigureAwait(false) is not { } database
            || database.ExpiresAt is not { } expiresAt || expiresAt > now || database.Home(options.Region) != options.Region)
        {
            return false;
        }

        if (await HomeMovePendingAsync(transaction, storage.DatabaseId!, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        _ = await UnregisterRecordAsync(transaction, storage, database, cancellationToken, customerDeletion: true).ConfigureAwait(false);
        return true;
    }
}

using TerraBase.Engine;
using TerraBase.Tenancy;
using ZLogger;

namespace TerraBase.Features.Management;

internal sealed partial class EngineManagementService
{
    /// <summary>
    /// One pass of this machine's catalog upkeep: purges, project closures, database expiry, the file audit and retried
    /// cleanups of deleted databases.
    /// </summary>
    internal async ValueTask RunMaintenanceAsync(CancellationToken cancellationToken)
    {
        if (!catalogGate.Value.IsOpen)
        {
            return;
        }

        await ConvergePurgesAsync(cancellationToken).ConfigureAwait(false);
        await ConvergeClosuresAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await ExpireDatabasesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (!cancellationToken.IsCancellationRequested)
        {
            logger.ZLogWarning(error, $"Deleting expired databases failed; the next pass tries again");
        }

        if (time.GetUtcNow() >= _nextFileAudit)
        {
            try
            {
                await AuditFilesAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error) when (!cancellationToken.IsCancellationRequested)
            {
                logger.ZLogWarning(error, $"Database file audit failed; the next scheduled audit retries");
            }
        }

        string? after = null;
        while (true)
        {
            var page = await ReadAsync<ManagementPage<TenantContext>?>(
                async transaction => await CatalogTables.PendingCleanupsAsync(transaction, after, CatalogPage, cancellationToken).ConfigureAwait(false),
                static _ => null, cancellationToken).ConfigureAwait(false);
            if (page is null || page.Items.Count == 0)
            {
                break;
            }

            foreach (var database in page.Items)
            {
                try
                {
                    await DropLocalAsync(database, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception error) when (!cancellationToken.IsCancellationRequested)
                {
                    logger.ZLogWarning(error, $"Retrying database cleanup {database.DatabaseId:@Database} failed; the next pass tries again");
                }
            }

            if (page.Next is null)
            {
                break;
            }

            after = page.Next;
        }
    }

    private async ValueTask DropLocalAsync(TenantContext database, CancellationToken cancellationToken)
    {
        var deleted = await ReadAsync<bool>(async transaction =>
            await HasDeletionAuthorityAsync(transaction, database, cancellationToken).ConfigureAwait(false),
            static _ => false, cancellationToken).ConfigureAwait(false);
        if (!deleted)
        {
            return;
        }

        var result = await DeleteRecordedAsync(database, PrepareAsync, cancellationToken).ConfigureAwait(false);
        if (result is not bool recorded || recorded)
        {
            return;
        }

        var resumed = await WriteAsync<bool>(transaction => PrepareAsync(transaction, cancellationToken), static _ => false, cancellationToken).ConfigureAwait(false);
        if (resumed)
        {
            _ = await FinishDatabaseCleanupAsync(database, CancellationToken.None).ConfigureAwait(false);
        }

        async ValueTask<bool> PrepareAsync(IStorageTransaction transaction, CancellationToken token)
        {
            if (!await HasDeletionAuthorityAsync(transaction, database, token).ConfigureAwait(false))
            {
                return false;
            }

            if (await CatalogTables.PendingCleanupAsync(transaction, database.DatabaseId!, token).ConfigureAwait(false) is not { } owner)
            {
                _ = await CatalogTables.PutPendingCleanupAsync(transaction, database, token).ConfigureAwait(false);
            }
            else if (owner.OrganizationId != database.OrganizationId || owner.ProjectId != database.ProjectId)
            {
                throw new InvalidOperationException("a database cleanup intent conflicts with the catalog");
            }

            await transaction.DeleteKindsAsync(SystemCatalog.DatabaseState(database.DatabaseId!), token).ConfigureAwait(false);
            return true;
        }
    }
}

internal sealed class CatalogMaintenance(EngineManagementService catalog, TimeProvider time, ILogger<CatalogMaintenance> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2), time);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                await catalog.RunMaintenanceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception error) when (!stoppingToken.IsCancellationRequested)
            {
                logger.ZLogWarning(error, $"Catalog maintenance failed; the next pass tries again");
            }
        }
    }
}

using ZLogger;
using TerraBase.Hosting;
using TerraBase.Tenancy;

namespace TerraBase.Features.Management;

/// <summary>
/// What the replicated catalog decides elsewhere, applied to this machine's database files (docs/terrabase/
/// multi-region-replication.md sections 15 and 18). Every machine writes the catalog locally, so another region may
/// delete a database this machine holds, or create one under a name this machine's database took at the same time. Each
/// pass looks at the catalog record of every local file:
/// <list type="bullet">
/// <item>a deleted record (a tombstone, which never comes back): the file goes here too (the database's catalog state came
/// deleted with the record);</item>
/// <item>a live record whose name the catalog's name index gives to another live database: the smaller ULID keeps the name
/// (every machine decides the same), so the larger one is deleted (its creator sees it gone: <c>name_taken</c>), and the
/// smaller one writes the index back to itself if a later write gave it away.</item>
/// </list>
/// A file whose record is missing is left alone: a creation in progress, or state the startup audit reports.
/// </summary>
internal sealed partial class EngineManagementService
{
    /// <summary>One pass over this machine's database files; a failure of one database leaves the others to the pass.</summary>
    internal async ValueTask ConvergeAsync(CancellationToken cancellationToken)
    {
        await foreach (var database in engine.ListDatabasesAsync(cancellationToken).ConfigureAwait(false))
        {
            try
            {
                await ConvergeDatabaseAsync(database, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error) when (!cancellationToken.IsCancellationRequested)
            {
                logger.ZLogWarning(error, $"Applying the catalog to database {database.DatabaseId:@Database} failed; the next pass tries again");
            }
        }

        await DrainPrefixDeletesAsync(cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask ConvergeDatabaseAsync(TenantContext database, CancellationToken cancellationToken)
    {
        var (projectId, databaseId) = (database.ProjectId!, database.DatabaseId!);
        var (deleted, found, named, rivalWins) = await ReadAsync<(bool Deleted, ManagedDatabase? Record, string? Named, bool RivalWins)>(async transaction =>
        {
            var version = await transaction.GetVersionAsync(Databases(projectId), databaseId, cancellationToken).ConfigureAwait(false);
            if (version is not { Deleted: false })
            {
                return (version is { Deleted: true }, null, null, false);
            }

            var record = await GetAsync(transaction, Databases(projectId), databaseId, TerraBaseJsonContext.Default.ManagedDatabase, cancellationToken).ConfigureAwait(false);
            var named = record is null ? null : await GetAsync(transaction, DatabaseNames(projectId), record.Name, Id, cancellationToken).ConfigureAwait(false);
            var rivalWins = named is not null && named != databaseId && string.CompareOrdinal(named, databaseId) < 0
                && await GetAsync(transaction, Databases(projectId), named, TerraBaseJsonContext.Default.ManagedDatabase, cancellationToken).ConfigureAwait(false) is { } rival
                && rival.Name == record!.Name;
            return (false, record, named, rivalWins);
        }, static _ => (false, null, null, false), cancellationToken).ConfigureAwait(false);

        if (deleted)
        {
            await DropLocalAsync(database, cancellationToken).ConfigureAwait(false);
        }
        else if (found is { } record && named != databaseId)
        {
            if (rivalWins)
            {
                await LoseNameAsync(database, record, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await ClaimNameAsync(record, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    // A database deleted in another region: its file here. Its catalog state came deleted with the deletion's tombstones.
    private async ValueTask DropLocalAsync(TenantContext database, CancellationToken cancellationToken)
    {
        if (await engine.OpenDatabaseAsync(database, cancellationToken).ConfigureAwait(false) is { } store)
        {
            checkpoint("converge.drop", database);
            _ = await engine.DeleteDatabaseAsync(database, cancellationToken, incarnation: store.Incarnation).ConfigureAwait(false);
            logger.ZLogInformation($"Database {database.DatabaseId:@Database} was deleted in another region; its file here is deleted too");
        }
    }

    // The same name was taken by a database of a smaller ULID at about the same time in another region: this one goes,
    // through the deletion every region applies, and the name stays with the winner.
    private async ValueTask LoseNameAsync(TenantContext database, ManagedDatabase record, CancellationToken cancellationToken)
    {
        var lost = await DeleteRecordedAsync(database, async (transaction, token) =>
        {
            var current = await GetAsync(transaction, Databases(record.ProjectId), record.Id, TerraBaseJsonContext.Default.ManagedDatabase, token).ConfigureAwait(false);
            var named = current is null ? null : await GetAsync(transaction, DatabaseNames(record.ProjectId), current.Name, Id, token).ConfigureAwait(false);
            if (current is null || named is null || string.CompareOrdinal(named, record.Id) >= 0
                || await GetAsync(transaction, Databases(record.ProjectId), named, TerraBaseJsonContext.Default.ManagedDatabase, token).ConfigureAwait(false) is not { } winner
                || winner.Name != current.Name)
            {
                return false;
            }

            _ = await UnregisterRecordAsync(transaction, database, current, token, releaseName: false).ConfigureAwait(false);
            return true;
        }, cancellationToken).ConfigureAwait(false);
        if (lost is true)
        {
            logger.ZLogWarning($"Database {record.Id:@Database} lost its name to a database of a smaller id created at the same time in another region; it is deleted (name_taken)");
        }
    }

    // This database keeps its name against every live rival: the index names it again.
    private async ValueTask ClaimNameAsync(ManagedDatabase record, CancellationToken cancellationToken) =>
        _ = await WriteAsync<bool>(async transaction =>
        {
            if (await GetAsync(transaction, Databases(record.ProjectId), record.Id, TerraBaseJsonContext.Default.ManagedDatabase, cancellationToken).ConfigureAwait(false) is not { } current)
            {
                return false;
            }

            var named = await GetAsync(transaction, DatabaseNames(record.ProjectId), current.Name, Id, cancellationToken).ConfigureAwait(false);
            if (named == current.Id
                || (named is not null && string.CompareOrdinal(named, current.Id) < 0
                    && await GetAsync(transaction, Databases(record.ProjectId), named, TerraBaseJsonContext.Default.ManagedDatabase, cancellationToken).ConfigureAwait(false) is { } rival
                    && rival.Name == current.Name))
            {
                return false;
            }

            await PutAsync(transaction, DatabaseNames(record.ProjectId), current.Name, current.Id, Id, cancellationToken).ConfigureAwait(false);
            return true;
        }, static _ => false, cancellationToken).ConfigureAwait(false);
}

/// <summary>Applies the replicated catalog to this machine's database files every few seconds (see <see cref="EngineManagementService.ConvergeAsync"/>).</summary>
internal sealed class CatalogConvergence(EngineManagementService catalog, TimeProvider time, ILogger<CatalogConvergence> logger) : BackgroundService
{
    private static readonly TimeSpan Pass = TimeSpan.FromSeconds(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Pass, time);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                await catalog.ConvergeAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception error) when (!stoppingToken.IsCancellationRequested)
            {
                // A pass that cannot list the files (a closing engine, a failing volume) never stops the host; the next one tries again.
                logger.ZLogWarning(error, $"Applying the catalog to this machine's database files failed; the next pass tries again");
            }
        }
    }
}

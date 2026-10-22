using System.Text.Json;
using TerraBase.Engine;
using TerraBase.Hosting;
using TerraBase.Tenancy;
using ZLogger;

namespace TerraBase.Features.Management;

internal sealed partial class EngineManagementService
{
    internal async ValueTask ConvergeAsync(CancellationToken cancellationToken)
    {
        if (!catalogGate.Value.IsOpen)
        {
            return;
        }

        // A failed file deletion may have removed the main file already; its durable intent remains discoverable.
        string? after = null;
        while (true)
        {
            var page = await ReadAsync<DocumentPage?>(async transaction => await transaction.ListAsync(
                PendingDatabaseCleanup, after, MemberPage, MaxPageBytes, cancellationToken).ConfigureAwait(false), static _ => null, cancellationToken).ConfigureAwait(false);
            if (page is null || page.Documents.Count == 0)
            {
                break;
            }

            foreach (var document in page.Documents)
            {
                try
                {
                    var database = JsonSerializer.Deserialize(document.Json.Span, TerraBaseJsonContext.Default.TenantContext)
                        ?? throw new InvalidOperationException("a database cleanup intent is invalid");
                    if (document.Id != database.DatabaseId)
                    {
                        throw new InvalidOperationException("a database cleanup intent conflicts with the catalog");
                    }

                    // Creation intents are shared too: only a verified deletion authorizes background cleanup.
                    await DropLocalAsync(database, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception error) when (!cancellationToken.IsCancellationRequested)
                {
                    logger.ZLogWarning(error, $"Retrying database cleanup {document.Id:@Database} failed; the next pass tries again");
                }
            }

            if (!page.HasMore)
            {
                break;
            }

            after = page.Documents[^1].Id;
        }

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
            var name = record is null ? null : await GetAsync(transaction, DatabaseNames(projectId), record.Name, Id, cancellationToken).ConfigureAwait(false);
            var wins = name is not null && name != databaseId && string.CompareOrdinal(name, databaseId) < 0
                && await GetAsync(transaction, Databases(projectId), name, TerraBaseJsonContext.Default.ManagedDatabase, cancellationToken).ConfigureAwait(false) is { } rival
                && rival.Name == record!.Name;
            return (false, record, name, wins);
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

    private async ValueTask DropLocalAsync(TenantContext database, CancellationToken cancellationToken)
    {
        // Missing rows can describe an unfinished creation or an unknown file; neither authorizes deletion.
        var deleted = await ReadAsync<bool>(async transaction =>
            await transaction.GetVersionAsync(Databases(database.ProjectId!), database.DatabaseId!, cancellationToken).ConfigureAwait(false) is { Deleted: true },
            static _ => false, cancellationToken).ConfigureAwait(false);
        if (!deleted)
        {
            return;
        }

        var store = await engine.OpenDatabaseAsync(database, cancellationToken).ConfigureAwait(false);
        var recorded = await WriteAsync<bool>(async transaction =>
        {
            if (await transaction.GetVersionAsync(Databases(database.ProjectId!), database.DatabaseId!, cancellationToken).ConfigureAwait(false) is not { Deleted: true })
            {
                return false;
            }

            if (await transaction.GetAsync(PendingDatabaseCleanup, database.DatabaseId!, cancellationToken).ConfigureAwait(false) is null)
            {
                await PutAsync(transaction, PendingDatabaseCleanup, database.DatabaseId!, database, TerraBaseJsonContext.Default.TenantContext, cancellationToken).ConfigureAwait(false);
            }

            // Purge before clearing the intent, so a failure after file removal still has durable retry work.
            await transaction.DeleteCollectionsAsync(SystemCatalog.DatabaseState(database.DatabaseId!), cancellationToken).ConfigureAwait(false);
            return true;
        }, static _ => false, cancellationToken).ConfigureAwait(false);
        if (recorded)
        {
            checkpoint("converge.drop", database);
            _ = await FinishDatabaseCleanupAsync(database, cancellationToken, incarnation: store?.Incarnation).ConfigureAwait(false);
        }
    }

    private async ValueTask LoseNameAsync(TenantContext database, ManagedDatabase record, CancellationToken cancellationToken) =>
        _ = await DeleteRecordedAsync(database, async (transaction, token) =>
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

internal sealed class CatalogConvergence(EngineManagementService catalog, TimeProvider time, ILogger<CatalogConvergence> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2), time);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                await catalog.ConvergeAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception error) when (!stoppingToken.IsCancellationRequested)
            {
                logger.ZLogWarning(error, $"Applying the catalog to this machine's database files failed; the next pass tries again");
            }
        }
    }
}

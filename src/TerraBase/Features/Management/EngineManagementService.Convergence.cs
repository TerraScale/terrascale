using System.Text.Json;
using TerraBase.Engine;
using TerraBase.Hosting;
using TerraBase.Tenancy;
using ZLogger;

namespace TerraBase.Features.Management;

internal sealed partial class EngineManagementService
{
    private const int MaxDatabaseName = 63;
    private const string ConflictActor = "system";

    internal async ValueTask ConvergeAsync(CancellationToken cancellationToken)
    {
        if (!catalogGate.Value.IsOpen)
        {
            return;
        }

        _quotaRegistered = _quotaRegistered || await RegisterQuotaRegionAsync(cancellationToken).ConfigureAwait(false);
        await ConvergePurgesAsync(cancellationToken).ConfigureAwait(false);
        await ConvergeClosuresAsync(cancellationToken).ConfigureAwait(false);
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
        var (deleted, found, named, winner, stale) = await ReadAsync<(bool Deleted, ManagedDatabase? Record, string? Named, string? Winner, bool Stale)>(async transaction =>
        {
            if (await HasDeletionAuthorityAsync(transaction, database, cancellationToken).ConfigureAwait(false))
            {
                return (true, null, null, null, false);
            }

            var version = await transaction.GetVersionAsync(Databases(projectId), databaseId, cancellationToken).ConfigureAwait(false);
            if (version is not { Deleted: false })
            {
                return (version is { Deleted: true }, null, null, null, false);
            }

            var record = await GetAsync(transaction, Databases(projectId), databaseId, TerraBaseJsonContext.Default.ManagedDatabase, cancellationToken).ConfigureAwait(false);
            if (record is null)
            {
                return (false, null, null, null, false);
            }

            var name = await GetAsync(transaction, DatabaseNames(projectId), record.Name, Id, cancellationToken).ConfigureAwait(false);
            var winner = await NameWinnerAsync(transaction, record, cancellationToken).ConfigureAwait(false);
            return (false, record, name, winner, await StaleNamesAsync(transaction, record, cancellationToken).ConfigureAwait(false) is not []);
        }, static _ => (false, null, null, null, false), cancellationToken).ConfigureAwait(false);

        if (deleted)
        {
            await DropLocalAsync(database, cancellationToken).ConfigureAwait(false);
        }
        else if (found is not { } record)
        {
            return;
        }
        else if (winner is not null)
        {
            // One writer per loser, its home region, so the move and its audit event are recorded once.
            if (record.Home(options.Region) == options.Region)
            {
                await KeepLoserAsync(record, database.OrganizationId, cancellationToken).ConfigureAwait(false);
            }
        }
        else if (named != databaseId)
        {
            await ClaimNameAsync(record, cancellationToken).ConfigureAwait(false);
        }
        else if (stale)
        {
            await DropStaleNamesAsync(record, cancellationToken).ConfigureAwait(false);
        }
    }

    // The smaller id holding the database's name in the index under the same name: that database keeps the name.
    private static async ValueTask<string?> NameWinnerAsync(IStorageTransaction transaction, ManagedDatabase record, CancellationToken cancellationToken) =>
        await GetAsync(transaction, DatabaseNames(record.ProjectId), record.Name, Id, cancellationToken).ConfigureAwait(false) is { } named
        && string.CompareOrdinal(named, record.Id) < 0
        && await GetAsync(transaction, Databases(record.ProjectId), named, TerraBaseJsonContext.Default.ManagedDatabase, cancellationToken).ConfigureAwait(false) is { } rival
        && rival.Name == record.Name
            ? named
            : null;

    // The database that lost its name keeps its id, data, backups, keys and file under the first free name every region
    // derives alike (docs/terrabase/database-name-conflicts.md). A name conflict never deletes a database.
    private async ValueTask KeepLoserAsync(ManagedDatabase record, string organizationId, CancellationToken cancellationToken) =>
        _ = await WriteAsync<bool>(async transaction =>
        {
            if (await GetAsync(transaction, Databases(record.ProjectId), record.Id, TerraBaseJsonContext.Default.ManagedDatabase, cancellationToken).ConfigureAwait(false) is not { } current
                || await NameWinnerAsync(transaction, current, cancellationToken).ConfigureAwait(false) is not { } winner)
            {
                return false;
            }

            // A database that loses a conflict name again keeps the name its customer chose, and the database that holds it.
            var kept = current.ConflictOf is null ? current with { PreviousName = current.Name, ConflictOf = winner } : current;
            return await MoveToFreeNameAsync(transaction, organizationId, kept, ConflictNames(kept.PreviousName ?? current.Name, current.Id), cancellationToken)
                .ConfigureAwait(false)
                ? true
                : throw new InvalidOperationException("every conflict name of a database that lost its name is held");
        }, static _ => false, cancellationToken).ConfigureAwait(false);

    // The names a database that lost `name` tries, in order: the name, cut to fit the database name rules, then
    // "-conflict-" and a longer tail of the database's own lowercase id each time. Every region derives the same list.
    private static IEnumerable<string> ConflictNames(string name, string id)
    {
        foreach (var length in (int[])[6, 8, 10, 13, 16, 20, 26])
        {
            var suffix = $"-conflict-{id[^Math.Min(length, id.Length)..].ToLowerInvariant()}";
            yield return name[..Math.Min(name.Length, MaxDatabaseName - suffix.Length)] + suffix;
        }
    }

    // Moves a database that lost a name race to the first of `candidates` no other database holds, in the caller's
    // transaction, and records the move in the organization's audit log. Its id, data, backups and file stay, and requests
    // by id keep working; the lost name serves its holder only. False when every candidate is held.
    private async ValueTask<bool> MoveToFreeNameAsync(
        IStorageTransaction transaction, string organizationId, ManagedDatabase database, IEnumerable<string> candidates, CancellationToken cancellationToken)
    {
        foreach (var name in candidates)
        {
            if (await NameHolderAsync(transaction, database.ProjectId, name, cancellationToken).ConfigureAwait(false) is { } holder && holder != database.Id)
            {
                continue;
            }

            await PutAsync(transaction, Databases(database.ProjectId), database.Id, database with { Name = name }, TerraBaseJsonContext.Default.ManagedDatabase, cancellationToken)
                .ConfigureAwait(false);
            await PutAsync(transaction, DatabaseNames(database.ProjectId), name, database.Id, Id, cancellationToken).ConfigureAwait(false);
            await RecordAsync(transaction, organizationId, ConflictActor, "database.name_conflict", database.Id,
                $"{database.PreviousName} is held by {database.ConflictOf}; this database is kept as {name}", cancellationToken).ConfigureAwait(false);
            return true;
        }

        return false;
    }

    // Index entries that still point at the database under a name it no longer carries: its previous name and its conflict names.
    private static async ValueTask<List<string>> StaleNamesAsync(IStorageTransaction transaction, ManagedDatabase record, CancellationToken cancellationToken)
    {
        List<string> stale = [];
        if (record.PreviousName is { } previous)
        {
            foreach (var name in ConflictNames(previous, record.Id).Prepend(previous))
            {
                if (name != record.Name && await GetAsync(transaction, DatabaseNames(record.ProjectId), name, Id, cancellationToken).ConfigureAwait(false) == record.Id)
                {
                    stale.Add(name);
                }
            }
        }

        return stale;
    }

    private async ValueTask DropStaleNamesAsync(ManagedDatabase record, CancellationToken cancellationToken) =>
        _ = await WriteAsync<bool>(async transaction =>
        {
            if (await GetAsync(transaction, Databases(record.ProjectId), record.Id, TerraBaseJsonContext.Default.ManagedDatabase, cancellationToken).ConfigureAwait(false) is not { } current)
            {
                return false;
            }

            foreach (var name in await StaleNamesAsync(transaction, current, cancellationToken).ConfigureAwait(false))
            {
                _ = await transaction.DeleteAsync(DatabaseNames(current.ProjectId), name, cancellationToken).ConfigureAwait(false);
            }

            return true;
        }, static _ => false, cancellationToken).ConfigureAwait(false);

    private async ValueTask DropLocalAsync(TenantContext database, CancellationToken cancellationToken)
    {
        // Missing rows can describe an unfinished creation or an unknown file; neither authorizes deletion.
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

        // A local reservation can outlive the shared intent a peer cleared while cleaning its own copy.
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

            if (await transaction.GetAsync(PendingDatabaseCleanup, database.DatabaseId!, token).ConfigureAwait(false) is not { } pending)
            {
                await PutAsync(transaction, PendingDatabaseCleanup, database.DatabaseId!, database, TerraBaseJsonContext.Default.TenantContext, token).ConfigureAwait(false);
            }
            else
            {
                var owner = JsonSerializer.Deserialize(pending.Json.Span, TerraBaseJsonContext.Default.TenantContext);
                if (owner is null || owner.OrganizationId != database.OrganizationId || owner.ProjectId != database.ProjectId || owner.DatabaseId != database.DatabaseId)
                {
                    throw new InvalidOperationException("a database cleanup intent conflicts with the catalog");
                }
            }

            // Purge before clearing the intent, so a failure after file removal still has durable retry work.
            await transaction.DeleteCollectionsAsync(SystemCatalog.DatabaseState(database.DatabaseId!), token).ConfigureAwait(false);
            return true;
        }
    }

    // The database that holds a name in the project: its index entry's, unless that database is deleted or now carries
    // another name. A database whose record has not replicated here yet still holds its name.
    private static async ValueTask<string?> NameHolderAsync(IStorageTransaction transaction, string projectId, string name, CancellationToken cancellationToken)
    {
        if (await GetAsync(transaction, DatabaseNames(projectId), name, Id, cancellationToken).ConfigureAwait(false) is not { } named
            || await transaction.GetVersionAsync(Databases(projectId), named, cancellationToken).ConfigureAwait(false) is { Deleted: true })
        {
            return null;
        }

        return await GetAsync(transaction, Databases(projectId), named, TerraBaseJsonContext.Default.ManagedDatabase, cancellationToken).ConfigureAwait(false) is { } holder
            && holder.Name != name
                ? null
                : named;
    }

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

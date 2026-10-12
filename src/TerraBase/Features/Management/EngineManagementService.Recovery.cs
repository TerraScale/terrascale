using System.Diagnostics.Metrics;
using System.Text.Json;
using TerraBase.Errors;
using TerraBase.Features.Management.Backups;
using TerraBase.Features.Replication;
using TerraBase.Hosting;
using TerraBase.Tenancy;
using ZLogger;

namespace TerraBase.Features.Management;

internal sealed partial class EngineManagementService
{
    private readonly Meter _recoveryMeter = meters.Create(Telemetry.MeterName);
    private long _unknownDatabaseFiles;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _recoveryMeter.CreateObservableGauge("terrabase.storage.unknown_files", () => _unknownDatabaseFiles,
            description: "Files found at startup without a registered database; preserved for operator recovery.");
        // Only the current catalog owns cleanup intents; older formats are refused.
        var (hasCatalog, version) = await CatalogStateAsync(cancellationToken).ConfigureAwait(false);
        if (version == CatalogVersion)
        {
            while (true)
            {
                // One bounded page at a time; release the catalog connection before deleting storage.
                var page = await ReadCatalogAsync(transaction => transaction.ListAsync(
                    PendingDatabaseCleanup, afterId: null, MemberPage, MaxPageBytes, cancellationToken), cancellationToken).ConfigureAwait(false);
                if (page.Documents.Count == 0)
                {
                    break;
                }

                foreach (var document in page.Documents)
                {
                    var storage = JsonSerializer.Deserialize(document.Json.Span, TerraBaseJsonContext.Default.TenantContext)
                        ?? throw new InvalidOperationException("a database cleanup intent is invalid");
                    if (document.Id != storage.DatabaseId)
                    {
                        // Inconsistent metadata cannot authorize deletion of registered data.
                        throw new InvalidOperationException("a database cleanup intent conflicts with the catalog");
                    }

                    checkpoint("recovery.intent", storage);
                    if (await FinishDatabaseCleanupAsync(storage, cancellationToken).ConfigureAwait(false) is Unavailable)
                    {
                        return;
                    }
                }
            }

            await DrainPrefixDeletesAsync(cancellationToken).ConfigureAwait(false);
        }

        await AuditFilesAsync(hasCatalog, version, cancellationToken).ConfigureAwait(false);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async ValueTask<(bool HasCatalog, string? Version)> CatalogStateAsync(CancellationToken cancellationToken)
    {
        var hasCatalog = engine.SystemDatabaseExists;
        return (hasCatalog, hasCatalog
            ? await ReadCatalogAsync(transaction => GetAsync(transaction, Catalog, CatalogVersionId, Id, cancellationToken), cancellationToken).ConfigureAwait(false)
            : null);
    }

    // Counts and logs files without a registered database; they are preserved for operator recovery.
    private async ValueTask AuditFilesAsync(bool hasCatalog, string? version, CancellationToken cancellationToken)
    {
        // ponytail: one page of fixed-size ULID keys bounds items/bytes; partition the scan if interleaved files defeat reuse.
        var registrations = new Dictionary<(string Organization, string Project, string Database), bool>();
        foreach (var file in engine.EnumerateDatabaseFiles(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var registered = false;
            // Future catalogs may change row schemas; preserve their files without interpreting those rows.
            if (hasCatalog && version == CatalogVersion
                && file.Database is { } database)
            {
                var key = (database.OrganizationId, database.ProjectId!, database.DatabaseId!);
                if (!registrations.TryGetValue(key, out registered))
                {
                    checkpoint("audit.registration", database);
                    registered = await IsRegisteredAsync(database, cancellationToken).ConfigureAwait(false);
                    if (registrations.Count == MemberPage)
                    {
                        registrations.Clear();
                    }

                    registrations.Add(key, registered);
                }
            }

            if (!registered)
            {
                _unknownDatabaseFiles++;
                logger.ZLogWarning($"Preserved database file without a catalog registration: {file.Path:@Path}");
            }
        }
    }

    // A database of the project, or a backup snapshot its organization still tracks there (the storage sweep deletes it).
    private ValueTask<bool> IsRegisteredAsync(TenantContext storage, CancellationToken cancellationToken) =>
        ReadCatalogAsync(async transaction =>
            await OrganizationProjectAsync(transaction, storage.OrganizationId, storage.ProjectId!, cancellationToken).ConfigureAwait(false) is not null
            && (await GetAsync(transaction, Databases(storage.ProjectId!), storage.DatabaseId!, TerraBaseJsonContext.Default.ManagedDatabase, cancellationToken).ConfigureAwait(false) is not null
                || await GetAsync(transaction, BackupCatalog.Records(storage.OrganizationId), storage.DatabaseId!, TerraBaseJsonContext.Default.BackupRecord, cancellationToken)
                    .ConfigureAwait(false) is { } backup && backup.ProjectId == storage.ProjectId),
            cancellationToken);

    private ValueTask<RecordedDeletion> HasDatabaseRowAsync(TenantContext storage, CancellationToken cancellationToken) =>
        ReadAsync<RecordedDeletion>(async transaction =>
            await GetAsync(transaction, Databases(storage.ProjectId!), storage.DatabaseId!, TerraBaseJsonContext.Default.ManagedDatabase, cancellationToken).ConfigureAwait(false) is not null,
            static error => error, cancellationToken);

    // Tombstones the catalog state of the databases and projects this machine deleted, at most PrefixDeleteChunk documents per
    // transaction. The intent goes in the transaction that finds nothing left, so a failure or restart between chunks resumes
    // here (after a deletion, at startup, in each convergence pass) and every committed tombstone stays: the catalog never
    // collects (docs/terrabase/system-catalog-invariants.md section 1). A commit drains after it commits.
    internal async ValueTask DrainPrefixDeletesAsync(CancellationToken cancellationToken)
    {
        if (CommitTransaction.Value is not null)
        {
            return;
        }

        while (true)
        {
            var page = await ReadCatalogAsync(transaction => transaction.ListAsync(
                PendingPrefixDeletes, afterId: null, MemberPage, MaxPageBytes, cancellationToken), cancellationToken).ConfigureAwait(false);
            if (page.Documents.Count == 0)
            {
                return;
            }

            foreach (var document in page.Documents)
            {
                long deleted;
                do
                {
                    checkpoint("prefix.chunk", ChangeGroups.System);
                    deleted = await WriteAsync(async transaction =>
                    {
                        var count = await transaction.DeleteCollectionsAsync(document.Id, PrefixDeleteChunk, cancellationToken).ConfigureAwait(false);
                        if (count == 0)
                        {
                            _ = await transaction.DeleteAsync(PendingPrefixDeletes, document.Id, cancellationToken).ConfigureAwait(false);
                        }

                        return count;
                    }, static _ => -1L, cancellationToken).ConfigureAwait(false);
                    if (deleted < 0)
                    {
                        return; // A catalog this build cannot use: the intent stays for a supported start.
                    }
                }
                while (deleted > 0);
            }
        }
    }

    private async ValueTask<CatalogCheck> FinishDatabaseCleanupAsync(TenantContext storage, CancellationToken cancellationToken)
    {
        // This also protects a create whose registration committed before a later error was reported. A registered database
        // keeps its files: another region may have registered the same chosen id while this machine's own creation of it
        // failed (the catalog replicates, docs/terrabase/multi-region-replication.md section 15), and the file here is then
        // its local copy. Only the intent goes.
        var registered = await HasDatabaseRowAsync(storage, cancellationToken).ConfigureAwait(false);
        if (registered is Unavailable refused)
        {
            return refused;
        }

        if (registered is bool exists && exists)
        {
            logger.ZLogInformation($"A cleanup intent names database {storage.DatabaseId:@Database}, which the catalog registers; its files are kept");
            return await ClearDatabaseCleanupAsync(storage, cancellationToken).ConfigureAwait(false);
        }

        await engine.DeleteDatabaseAsync(storage, cancellationToken, retainIntentOwnership: true).ConfigureAwait(false);
        checkpoint("cleanup.files", storage);
        return await ClearDatabaseCleanupAsync(storage, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<CatalogCheck> ClearDatabaseCleanupAsync(TenantContext storage, CancellationToken cancellationToken)
    {
        try
        {
            checkpoint("cleanup.clearing", storage);
            var cleared = await WriteAsync<CatalogCheck>(async transaction =>
            {
                await transaction.DeleteAsync(PendingDatabaseCleanup, storage.DatabaseId!, cancellationToken).ConfigureAwait(false);
                return new Done();
            }, static error => error, cancellationToken).ConfigureAwait(false);
            if (cleared is Unavailable refused)
            {
                return refused;
            }

            checkpoint("cleanup.committed", storage);
        }
        catch
        {
            // Intent deletion can commit before transaction disposal reports an error.
            await ReleaseUnrecordedIntentAsync(storage).ConfigureAwait(false);
            throw;
        }

        await engine.CompleteDatabaseIntentAsync(storage, CancellationToken.None).ConfigureAwait(false);
        return new Done();
    }

    private async ValueTask ReleaseUnrecordedIntentAsync(TenantContext storage)
    {
        var recorded = await ReadAsync<RecordedDeletion>(async transaction =>
            await transaction.GetAsync(PendingDatabaseCleanup, storage.DatabaseId!, CancellationToken.None).ConfigureAwait(false) is not null,
            static error => error, CancellationToken.None).ConfigureAwait(false);
        if (recorded is bool exists && !exists)
        {
            // No durable intent remains to retain the destination reservation.
            await engine.CompleteDatabaseIntentAsync(storage, CancellationToken.None).ConfigureAwait(false);
        }
    }
}

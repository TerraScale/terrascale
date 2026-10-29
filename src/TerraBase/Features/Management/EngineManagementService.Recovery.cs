using System.Diagnostics.Metrics;
using System.Text.Json;
using TerraBase.Engine;
using TerraBase.Errors;
using TerraBase.Features.Management.Backups;
using TerraBase.Hosting;
using TerraBase.Tenancy;
using ZLogger;

namespace TerraBase.Features.Management;

internal sealed partial class EngineManagementService
{
    private readonly Meter _recoveryMeter = meters.Create(Telemetry.MeterName);
    private long _unknownDatabaseFiles;
    private long _unknownDatabaseBytes;
    private readonly TaskCompletionSource _recoveryStopping = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _deferredRecovery;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        RegisterPurgeMetrics();
        _recoveryMeter.CreateObservableGauge("terrabase.storage.unknown_files", () => Interlocked.Read(ref _unknownDatabaseFiles),
            description: "Unowned files preserved for operator recovery.");
        _recoveryMeter.CreateObservableGauge("terrabase.storage.unknown_bytes", () => Interlocked.Read(ref _unknownDatabaseBytes),
            unit: "By", description: "Bytes in unowned files preserved for operator recovery.");
        if (catalogGate.Value.IsOpen)
        {
            await RecoverAsync(only: null, cancellationToken).ConfigureAwait(false);
            return;
        }

        // A restored catalog cannot authorize deleting files before it has been verified, and a cell's gate opens only once
        // its peers verified the catalog, after this start: recover then. Skipping it left every abandoned creation's file
        // on disk and audited none (TS-994). Only intents naming a file here now: no creation starts before the gate opens,
        // and a peer's creation intent, replicated here, names no local file.
        HashSet<string> local = [.. engine.EnumerateDatabaseFiles(cancellationToken).Select(file => file.Database?.DatabaseId).OfType<string>()];
        _deferredRecovery = RecoverOnceOpenAsync(local);
    }

    // Bounded as the recovery at start is; a stop before the gate opened leaves it to the next start.
    private async Task RecoverOnceOpenAsync(IReadOnlySet<string> local)
    {
        try
        {
            if (await Task.WhenAny(catalogGate.Value.Opened, _recoveryStopping.Task).ConfigureAwait(false) == catalogGate.Value.Opened)
            {
                await RecoverAsync(local, CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception error)
        {
            logger.ZLogError(error, $"Recovering database cleanup intents after the catalog gate opened failed; the next start tries again");
        }
    }

    // Finishes the cleanup intents (all, or those of `only`) and audits the files, once the catalog is verified.
    private async Task RecoverAsync(IReadOnlySet<string>? only, CancellationToken cancellationToken)
    {
        // Only the current catalog owns cleanup intents; older formats are refused.
        var (_, version) = await CatalogStateAsync(cancellationToken).ConfigureAwait(false);
        var retried = false;
        string? after = null;
        if (version == CatalogVersion)
        {
            while (!retried)
            {
                // One bounded page at a time; release the catalog connection before deleting storage.
                var page = await ReadCatalogAsync(transaction => transaction.ListAsync(
                    PendingDatabaseCleanup, after, CatalogPage, MaxPageBytes, cancellationToken), cancellationToken).ConfigureAwait(false);
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

                    if (only is not null && !only.Contains(storage.DatabaseId))
                    {
                        continue;
                    }

                    checkpoint("recovery.intent", storage);
                    if (await FinishDatabaseCleanupAsync(storage, cancellationToken, intent: document).ConfigureAwait(false) is Unavailable)
                    {
                        // An unavailable or unsupported catalog cannot authorize an audit against this version either.
                        return;
                    }

                    if (await ReadCatalogAsync(async transaction =>
                            await transaction.GetAsync(PendingDatabaseCleanup, storage.DatabaseId!, cancellationToken).ConfigureAwait(false) is not null,
                            cancellationToken).ConfigureAwait(false))
                    {
                        // A still-closing copy or withheld receipt is retried by convergence without spinning during startup;
                        // the file audit below still runs.
                        retried = true;
                        break;
                    }
                }

                if (!page.HasMore)
                {
                    break;
                }

                after = page.Documents[^1].Id;
            }
        }

        await AuditFilesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _recoveryStopping.TrySetResult();
        if (_deferredRecovery is { } deferred)
        {
            await deferred.ConfigureAwait(false);
        }
    }

    private async ValueTask<(bool HasCatalog, string? Version)> CatalogStateAsync(CancellationToken cancellationToken)
    {
        var hasCatalog = engine.SystemDatabaseExists;
        return (hasCatalog, hasCatalog
            ? await ReadCatalogAsync(transaction => GetAsync(transaction, Catalog, CatalogVersionId, Id, cancellationToken), cancellationToken).ConfigureAwait(false)
            : null);
    }

    // A database of the project, or a backup snapshot its organization still tracks there.
    public ValueTask<RegisteredDatabaseResult> RegisteredFileAsync(TenantContext storage, CancellationToken cancellationToken) =>
        !catalogGate.Value.IsOpen ? ValueTask.FromResult<RegisteredDatabaseResult>(UnreadableCatalog) :
        ReadAsync<RegisteredDatabaseResult>(async transaction => await IsRegisteredAsync(transaction, storage, cancellationToken).ConfigureAwait(false),
            static error => error, cancellationToken);

    private static async ValueTask<bool> IsRegisteredAsync(IStorageTransaction transaction, TenantContext storage, CancellationToken cancellationToken) =>
        await OrganizationProjectAsync(transaction, storage.OrganizationId, storage.ProjectId!, cancellationToken).ConfigureAwait(false) is not null
        && (await GetAsync(transaction, Databases(storage.ProjectId!), storage.DatabaseId!, TerraBaseJsonContext.Default.ManagedDatabase, cancellationToken).ConfigureAwait(false) is not null
            || await GetAsync(transaction, BackupCatalog.Records(storage.OrganizationId), storage.DatabaseId!, TerraBaseJsonContext.Default.BackupRecord, cancellationToken)
                .ConfigureAwait(false) is { } backup && backup.ProjectId == storage.ProjectId);

    private readonly union CleanupRegistration(bool, Done, Unavailable);

    private static bool SameCleanupIntent(StoredDocument? current, StoredDocument expected) =>
        current is not null && current.Id == expected.Id && current.Version == expected.Version && current.Scope == expected.Scope
        && current.Json.Span.SequenceEqual(expected.Json.Span);

    private async ValueTask<CatalogCheck> FinishDatabaseCleanupAsync(
        TenantContext storage, CancellationToken cancellationToken, bool abandonedCreation = false, StoredDocument? intent = null)
    {
        // A paged intent can be cleared by registration, or replaced, before recovery reaches it.
        // Revalidate its identity and registration in one snapshot before authorizing either deletion or conflict.
        var registered = await ReadAsync<CleanupRegistration>(async transaction =>
        {
            if (intent is not null && !SameCleanupIntent(
                await transaction.GetAsync(PendingDatabaseCleanup, storage.DatabaseId!, cancellationToken).ConfigureAwait(false), intent))
            {
                return new Done();
            }

            return await GetAsync(transaction, Databases(storage.ProjectId!), storage.DatabaseId!, TerraBaseJsonContext.Default.ManagedDatabase, cancellationToken).ConfigureAwait(false) is not null;
        }, static error => error, cancellationToken).ConfigureAwait(false);
        if (registered is Unavailable refused)
        {
            return refused;
        }

        if (registered is Done)
        {
            // Stale work owns neither the files nor any current intent's engine reservation.
            return new Done();
        }

        if (registered is bool exists && exists)
        {
            if (abandonedCreation)
            {
                // Replication can register the prepared copy before its local create is rejected.
                return await ClearDatabaseCleanupAsync(storage, erasureVerified: false, cancellationToken, intent).ConfigureAwait(false);
            }

            // A peer can publish its creation intent before another peer's registration reaches this snapshot.
            // Preserve both the live copy and the shared intent. Neither a cleanup nor an absence receipt is justified;
            // startup's bounded retry and background convergence revisit it after authoritative deletion arrives.
            return new Done();
        }

        var authority = await ReadAsync<RecordedDeletion>(async transaction =>
        {
            var purge = await GetAsync(transaction, DatabasePurge.Collection, storage.DatabaseId!, TerraBaseJsonContext.Default.DatabasePurgeRecord, cancellationToken).ConfigureAwait(false);
            if (purge is not null && (purge.FormatVersion != 1 || !DatabasePurge.SameDatabase(purge.Database, storage)))
            {
                throw new InvalidOperationException("a database purge conflicts with its cleanup scope");
            }
            return purge is not null;
        }, static error => error, cancellationToken).ConfigureAwait(false);
        if (authority is Unavailable unavailable)
        {
            return unavailable;
        }

        await engine.DeleteDatabaseAsync(storage, cancellationToken, retainIntentOwnership: true).ConfigureAwait(false);
        checkpoint("cleanup.files", storage);
        var erasureVerified = authority is true
            && await webhookErasure.Value.EraseAsync(storage, cancellationToken).ConfigureAwait(false)
            && await engine.VerifyDatabaseErasureAsync(storage, cancellationToken).ConfigureAwait(false);
        if (authority is true && !erasureVerified)
        {
            // Keep the committed deletion pending until webhook payloads and every native handle are gone.
            return new Done();
        }

        return await ClearDatabaseCleanupAsync(storage, erasureVerified, cancellationToken, intent).ConfigureAwait(false);
    }

    private async ValueTask<CatalogCheck> ClearDatabaseCleanupAsync(TenantContext storage, bool erasureVerified, CancellationToken cancellationToken, StoredDocument? intent = null)
    {
        try
        {
            checkpoint("cleanup.clearing", storage);
            var cleared = await WriteAsync<RecordedDeletion>(async transaction =>
            {
                if (intent is not null && !SameCleanupIntent(
                    await transaction.GetAsync(PendingDatabaseCleanup, storage.DatabaseId!, cancellationToken).ConfigureAwait(false), intent))
                {
                    return false;
                }

                if (!await TryRecordLocalPurgeAbsenceAsync(transaction, storage, erasureVerified, cancellationToken).ConfigureAwait(false))
                {
                    // Authority can arrive after the earlier read; retain the intent until verified on retry.
                    return false;
                }

                await transaction.DeleteAsync(PendingDatabaseCleanup, storage.DatabaseId!, cancellationToken).ConfigureAwait(false);
                return true;
            }, static error => error, cancellationToken).ConfigureAwait(false);
            if (cleared is Unavailable refused)
            {
                return refused;
            }
            if (cleared is false)
            {
                return new Done();
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

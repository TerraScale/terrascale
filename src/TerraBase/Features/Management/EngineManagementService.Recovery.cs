using System.Diagnostics.Metrics;
using TerraBase.Engine;
using TerraBase.Errors;
using TerraBase.Features.Management.Backups;
using TerraBase.Features.Management;
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
        RegisterClosureMetrics();
        _recoveryMeter.CreateObservableGauge("terrabase.storage.unknown_files", () => Interlocked.Read(ref _unknownDatabaseFiles),
            description: "Unowned files preserved for operator recovery.");
        _recoveryMeter.CreateObservableGauge("terrabase.storage.unknown_bytes", () => Interlocked.Read(ref _unknownDatabaseBytes),
            unit: "By", description: "Bytes in unowned files preserved for operator recovery.");
        if (catalogGate.Value.IsOpen)
        {
            await RecoverAsync(only: null, cancellationToken).ConfigureAwait(false);
            return;
        }

        HashSet<string> local = [.. engine.EnumerateDatabaseFiles(cancellationToken).Select(file => file.Database?.DatabaseId).OfType<string>()];
        _deferredRecovery = RecoverOnceOpenAsync(local);
    }

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

    private async Task RecoverAsync(IReadOnlySet<string>? only, CancellationToken cancellationToken)
    {
        var (_, version) = await CatalogStateAsync(cancellationToken).ConfigureAwait(false);
        var retried = false;
        string? after = null;
        if (version == CatalogVersion)
        {
            while (!retried)
            {
                var page = await ReadCatalogAsync(transaction => CatalogTables.PendingCleanupsAsync(transaction, after, CatalogPage, cancellationToken), cancellationToken)
                    .ConfigureAwait(false);
                if (page.Items.Count == 0)
                {
                    break;
                }

                foreach (var storage in page.Items)
                {
                    if (only is not null && !only.Contains(storage.DatabaseId!))
                    {
                        continue;
                    }

                    checkpoint("recovery.intent", storage);
                    if (await FinishDatabaseCleanupAsync(storage, cancellationToken, intent: storage).ConfigureAwait(false) is Unavailable)
                    {
                        return;
                    }

                    if (await ReadCatalogAsync(async transaction =>
                            await CatalogTables.PendingCleanupAsync(transaction, storage.DatabaseId!, cancellationToken).ConfigureAwait(false) is not null,
                            cancellationToken).ConfigureAwait(false))
                    {
                        retried = true;
                        break;
                    }
                }

                if (page.Next is null)
                {
                    break;
                }

                after = page.Next;
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

    public ValueTask<RegisteredDatabaseResult> RegisteredFileAsync(TenantContext storage, CancellationToken cancellationToken) =>
        !catalogGate.Value.IsOpen ? ValueTask.FromResult<RegisteredDatabaseResult>(UnreadableCatalog) :
        ReadAsync<RegisteredDatabaseResult>(async transaction => await IsRegisteredAsync(transaction, storage, cancellationToken).ConfigureAwait(false),
            static error => error, cancellationToken);

    private static async ValueTask<bool> IsRegisteredAsync(IStorageTransaction transaction, TenantContext storage, CancellationToken cancellationToken) =>
        await OrganizationProjectAsync(transaction, storage.OrganizationId, storage.ProjectId!, cancellationToken).ConfigureAwait(false) is not null
        && (await CatalogTables.DatabaseAsync(transaction, storage.ProjectId!, storage.DatabaseId!, cancellationToken).ConfigureAwait(false) is not null
            || await GetAsync(transaction, BackupCatalog.Records(storage.OrganizationId), storage.DatabaseId!, ManagementJsonContext.Default.BackupRecord, cancellationToken)
                .ConfigureAwait(false) is { } backup && backup.ProjectId == storage.ProjectId);

    private readonly union CleanupRegistration(bool, Done, Unavailable);

    private static bool SameCleanupIntent(TenantContext? current, TenantContext expected) => current == expected;

    private async ValueTask<CatalogCheck> FinishDatabaseCleanupAsync(
        TenantContext storage, CancellationToken cancellationToken, bool abandonedCreation = false, TenantContext? intent = null)
    {
        var registered = await ReadAsync<CleanupRegistration>(async transaction =>
        {
            if (intent is not null && !SameCleanupIntent(
                await CatalogTables.PendingCleanupAsync(transaction, storage.DatabaseId!, cancellationToken).ConfigureAwait(false), intent))
            {
                return new Done();
            }

            if (await GetAsync(transaction, DatabasePurge.Kind, storage.DatabaseId!, ManagementJsonContext.Default.DatabasePurgeRecord, cancellationToken).ConfigureAwait(false) is { } purge)
            {
                if (purge.FormatVersion != 1 || !DatabasePurge.SameDatabase(purge.Database, storage))
                {
                    throw new InvalidOperationException("a database purge conflicts with its cleanup scope");
                }
                return false;
            }
            return await transaction.GetAsync(DeletedDatabases.Kind, storage.DatabaseId!, cancellationToken).ConfigureAwait(false) is not null
                || await CatalogTables.DatabaseAsync(transaction, storage.ProjectId!, storage.DatabaseId!, cancellationToken).ConfigureAwait(false) is not null;
        }, static error => error, cancellationToken).ConfigureAwait(false);
        if (registered is Unavailable refused)
        {
            return refused;
        }

        if (registered is Done)
        {
            return new Done();
        }

        if (registered is bool exists && exists)
        {
            if (abandonedCreation)
            {
                return await ClearDatabaseCleanupAsync(storage, erasureVerified: false, cancellationToken, intent).ConfigureAwait(false);
            }

            return new Done();
        }

        var authority = await ReadAsync<RecordedDeletion>(async transaction =>
        {
            var purge = await GetAsync(transaction, DatabasePurge.Kind, storage.DatabaseId!, ManagementJsonContext.Default.DatabasePurgeRecord, cancellationToken).ConfigureAwait(false);
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
            && await engine.VerifyDatabaseErasureAsync(storage, cancellationToken).ConfigureAwait(false);
        if (authority is true && !erasureVerified)
        {
            return new Done();
        }

        return await ClearDatabaseCleanupAsync(storage, erasureVerified, cancellationToken, intent).ConfigureAwait(false);
    }

    private async ValueTask<CatalogCheck> ClearDatabaseCleanupAsync(TenantContext storage, bool erasureVerified, CancellationToken cancellationToken, TenantContext? intent = null)
    {
        try
        {
            checkpoint("cleanup.clearing", storage);
            var cleared = await WriteAsync<RecordedDeletion>(async transaction =>
            {
                if (intent is not null && !SameCleanupIntent(
                    await CatalogTables.PendingCleanupAsync(transaction, storage.DatabaseId!, cancellationToken).ConfigureAwait(false), intent))
                {
                    return false;
                }

                if (!await TryRecordLocalPurgeAbsenceAsync(transaction, storage, erasureVerified, cancellationToken).ConfigureAwait(false))
                {
                    return false;
                }

                _ = await CatalogTables.DeletePendingCleanupAsync(transaction, storage.DatabaseId!, cancellationToken).ConfigureAwait(false);
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
            await ReleaseUnrecordedIntentAsync(storage).ConfigureAwait(false);
            throw;
        }

        await engine.CompleteDatabaseIntentAsync(storage, CancellationToken.None).ConfigureAwait(false);
        return new Done();
    }

    private async ValueTask ReleaseUnrecordedIntentAsync(TenantContext storage)
    {
        var recorded = await ReadAsync<RecordedDeletion>(async transaction =>
            await CatalogTables.PendingCleanupAsync(transaction, storage.DatabaseId!, CancellationToken.None).ConfigureAwait(false) is not null,
            static error => error, CancellationToken.None).ConfigureAwait(false);
        if (recorded is bool exists && !exists)
        {
            await engine.CompleteDatabaseIntentAsync(storage, CancellationToken.None).ConfigureAwait(false);
        }
    }
}

using Amazon.Runtime;
using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Text.Json.Serialization;
using System.Text.Json;
using TerraBase.Engine;
using TerraBase.Features.Backup;
using TerraBase.Features.Management.Backups;
using TerraBase.Features.Management;
using TerraBase.Hosting;
using TerraBase.Tenancy;
using ZLogger;

namespace TerraBase.Features.Backup;

internal sealed record BackupPurgeProgress(DateTimeOffset RequestedAt, string? DestinationAfter, DateTimeOffset? ObjectsAbsentAt,
    DateTimeOffset? LastAttemptAt, string? Failure, bool InventoryFailed = false, DateTimeOffset? KeysShreddedAt = null, string? ShapeAfter = null, bool CopiesAbsent = false);

[JsonSerializable(typeof(BackupPurgeProgress))]
[JsonSerializable(typeof(PrefixPurgeProgress))]
[JsonSerializable(typeof(ShredRecord))]
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
internal sealed partial class BackupPurgeJsonContext : JsonSerializerContext;

public interface IBackupKeyShredding
{
    void Shred(TenantContext database);

    Task<bool> DestroyAsync(TenantContext database, CancellationToken cancellationToken);
}

internal sealed class DatabaseBackupPurge(
    SystemCatalog catalog, S3ObjectStore platform, DataKeys platformKeys, BackupTargets targets, TimeProvider time,
    IMeterFactory meters, ILogger<DatabaseBackupPurge> logger, KeyCustody custody, IServiceProvider services) : BackgroundService, IBackupKeyShredding
{
    private readonly CancellationTokenSource _stopping = new();
    internal const string ProgressKind = "backup_purge_progress";
    private readonly SemaphoreSlim _pass = new(1, 1);
    private readonly Counter<long> _failures = meters.Create(Telemetry.MeterName)
        .CreateCounter<long>("terrabase.backup.purge_failures", description: "Backup erasure attempts that remain unverified, by fixed reason.");
    private readonly PendingErasures _pending = new(meters.Create(Telemetry.MeterName), time);
    private string? _after;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1), time);
        do
        {
            try
            {
                await RunOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception) when (!stoppingToken.IsCancellationRequested)
            {
                _failures.Add(1, new KeyValuePair<string, object?>("reason", "catalog_unavailable"));
                logger.ZLogWarning($"Backup erasure inventory failed; durable work remains pending");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    internal async Task RunOnceAsync(CancellationToken token)
    {
        await _pass.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var page = await catalog.ListDatabasePurgesAsync(_after, 16, token).ConfigureAwait(false);
            if (page is null)
            {
                return;
            }

            foreach (var job in page.Items)
            {
                var progress = await catalog.ReadUncheckedAsync(transaction => SystemCatalog.GetAsync(transaction, ProgressKind,
                    job.Database.DatabaseId!, BackupPurgeJsonContext.Default.BackupPurgeProgress, token), token).ConfigureAwait(false)
                    ?? new BackupPurgeProgress(job.RequestedAt, null, null, null, null);
                if (job.BackupVerifiedAt is not null && progress.KeysShreddedAt is not null)
                {
                    _pending.Verified(job.Database.DatabaseId!);
                    continue;
                }
                try
                {
                    progress = await PurgeAsync(job, progress, token).ConfigureAwait(false);
                }
                catch (Exception) when (!token.IsCancellationRequested)
                {
                    progress = progress with { ObjectsAbsentAt = null, Failure = "storage_unavailable", InventoryFailed = true };
                    _failures.Add(1, new KeyValuePair<string, object?>("reason", "storage_unavailable"));
                    if (_pending.Report(job.Database.DatabaseId!, "inventory", "storage_unavailable"))
                    {
                        logger.ZLogWarning($"Backup erasure of database {job.Database.DatabaseId:@Database} remains pending after a storage failure");
                    }
                }

                await SaveAsync(job, progress with { LastAttemptAt = time.GetUtcNow() }, token).ConfigureAwait(false);
                if (progress.Failure is null)
                {
                    _pending.Verified(job.Database.DatabaseId!);
                    var hours = Math.Round((time.GetUtcNow() - job.RequestedAt).TotalHours, 1);
                    logger.ZLogInformation($"Backup erasure of database {job.Database.DatabaseId:@Database} is verified, {hours:@Hours} h after its deletion");
                }
                else
                {
                    _pending.Pending(job.Database.DatabaseId!, job.RequestedAt);
                    if (_pending.Report(job.Database.DatabaseId!, "reason", progress.Failure))
                    {
                        var holders = progress.Failure == "active_copies_pending"
                            ? string.Join(", ", await catalog.ReadUncheckedAsync(transaction => DatabasePurge.UnacknowledgedHoldersAsync(transaction, job, token), token).ConfigureAwait(false))
                            : "";
                        var hours = Math.Round((time.GetUtcNow() - job.RequestedAt).TotalHours, 1);
                        logger.ZLogInformation($"Backup erasure of database {job.Database.DatabaseId:@Database} waits: {progress.Failure:@Reason} {holders:@Holders}, {hours:@Hours} h after its deletion");
                    }
                }
            }

            foreach (var job in page.Items.Where(job => job.BackupVerifiedAt is not null))
            {
                _pending.Verified(job.Database.DatabaseId!);
            }

            _after = page.Next;
            if (_after is null)
            {
                _pending.Wrapped();
            }
        }
        finally
        {
            _pass.Release();
        }
    }

    public async Task<bool> DestroyAsync(TenantContext database, CancellationToken cancellationToken)
    {
        try
        {
            return await ShredAsync(database, cancellationToken).ConfigureAwait(false) is not null;
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            _failures.Add(1, new KeyValuePair<string, object?>("reason", "key_shred_unavailable"));
            return false;
        }
    }

    public void Shred(TenantContext database) => _ = ShredNowAsync(database);

    private async Task ShredNowAsync(TenantContext database)
    {
        try
        {
            await Task.Yield();
            if (await ShredAsync(database, _stopping.Token).ConfigureAwait(false) is null)
            {
                logger.ZLogInformation($"Backup keys of deleted database {database.DatabaseId:@Database} are not all destroyed yet; the erasure pass retries");
            }
        }
        catch (Exception) when (!_stopping.IsCancellationRequested)
        {
            _failures.Add(1, new KeyValuePair<string, object?>("reason", "key_shred_unavailable"));
            logger.ZLogWarning($"Shredding the backup keys of deleted database {database.DatabaseId:@Database} failed; the erasure pass retries");
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    internal async Task<DateTimeOffset?> ShredAsync(TenantContext database, CancellationToken token)
    {
        string? after = null;
        do
        {
            var page = await catalog.ReadUncheckedAsync(transaction => UnshreddedSnapshotsAsync(transaction, database, after, token), token).ConfigureAwait(false);
            foreach (var snapshot in page.Items)
            {
                if (await ShredScopeAsync(snapshot, token).ConfigureAwait(false) is null)
                {
                    return null;
                }
            }
            after = page.Next;
        }
        while (after is not null);

        return await ShredScopeAsync(database, token).ConfigureAwait(false);
    }

    private static async ValueTask<ManagementPage<TenantContext>> UnshreddedSnapshotsAsync(IStorageTransaction transaction, TenantContext database, string? after, CancellationToken token)
    {
        var page = await SystemCatalog.ListAsync(transaction, BackupCatalog.Records(database.OrganizationId), after, 100,
            ManagementJsonContext.Default.BackupRecord, record => record.Id, token).ConfigureAwait(false);
        List<TenantContext> snapshots = [];
        foreach (var snapshot in page.Items.Where(record => record.ProjectId == database.ProjectId && record.DatabaseId == database.DatabaseId))
        {
            if ((await SystemCatalog.GetAsync(transaction, ProgressKind, snapshot.Id, BackupPurgeJsonContext.Default.BackupPurgeProgress, token)
                .ConfigureAwait(false))?.KeysShreddedAt is null)
            {
                snapshots.Add(snapshot.Storage(database.Plan));
            }
        }

        return new(snapshots, page.Next);
    }

    private async Task<DateTimeOffset?> ShredScopeAsync(TenantContext database, CancellationToken token)
    {
        var scope = BackupLayout.KeyScope(database);
        platformKeys.Forget(scope);
        var (record, complete) = await custody.ShredAsync(database, time.GetUtcNow(), token).ConfigureAwait(false);
        platformKeys.Forget(scope);
        if (!complete)
        {
            return null;
        }

        await services.GetRequiredService<KeyRevocationPeers>().RevokeAsync(database, token).ConfigureAwait(false);

        if (!await services.GetRequiredService<Shapes.ShapeMirror>().DeleteIndexedDatabaseAsync(database, token).ConfigureAwait(false))
        {
            return null;
        }

        logger.ZLogInformation($"Backup keys of deleted database {database.DatabaseId:@Database} are destroyed");
        return await catalog.WriteDurableAsync<DateTimeOffset?>(async transaction =>
        {
            var progress = await SystemCatalog.GetAsync(transaction, ProgressKind, database.DatabaseId!,
                BackupPurgeJsonContext.Default.BackupPurgeProgress, token).ConfigureAwait(false)
                ?? new BackupPurgeProgress(record.ShreddedAt, null, null, null, null);
            await SystemCatalog.PutAsync(transaction, ProgressKind, database.DatabaseId!,
                progress with { KeysShreddedAt = record.ShreddedAt }, BackupPurgeJsonContext.Default.BackupPurgeProgress, token).ConfigureAwait(false);
            return record.ShreddedAt;
        }, static _ => null, token).ConfigureAwait(false);
    }

    private async Task<BackupPurgeProgress> PurgeAsync(DatabasePurgeRecord job, BackupPurgeProgress progress, CancellationToken token)
    {
        if (progress.KeysShreddedAt is null && await ShredAsync(job.Database, token).ConfigureAwait(false) is { } shredded)
        {
            progress = progress with { KeysShreddedAt = shredded };
        }

        if (!progress.CopiesAbsent)
        {
            await services.GetRequiredService<HomePush.HomePush>().PurgeDeletedAsync(job.Database, token).ConfigureAwait(false);
            await services.GetRequiredService<Shapes.ShapeOverlay>().PurgeDeletedAsync(job.Database, token).ConfigureAwait(false);
            await services.GetRequiredService<LocalAck.HoldStore>().DeleteDatabaseAsync(job.Database, token).ConfigureAwait(false);
            var shape = await services.GetRequiredService<Shapes.ShapeMirror>().DeleteDatabaseAsync(job.Database, progress.ShapeAfter, token).ConfigureAwait(false);
            progress = progress with { ShapeAfter = shape.After, CopiesAbsent = shape.Complete };
            if (!shape.Complete)
            {
                return progress with { Failure = "shape_copies_pending" };
            }
        }

        if (!await catalog.AreActiveCopiesAbsentAsync(job.Database, token).ConfigureAwait(false))
        {
            return progress with { ObjectsAbsentAt = null, Failure = "active_copies_pending" };
        }

        var restoring = await catalog.ReadUncheckedAsync(
            transaction => RestoreGenerationLeases.ActiveAsync(
                transaction, job.Database.OrganizationId, job.Database.DatabaseId!, null, time.GetUtcNow(), token),
            token).ConfigureAwait(false);
        if (restoring.Count > 0)
        {
            return progress with { ObjectsAbsentAt = null, Failure = "restore_in_progress" };
        }

        var prefix = BackupLayout.FileRoot(job.Database) + "/";
        var failed = progress.InventoryFailed;
        failed |= !await AttemptAsync(job, BackupDestinationNames.Platform, null, prefix, progress.ObjectsAbsentAt, token).ConfigureAwait(false);
        var destinations = await catalog.ReadUncheckedAsync(transaction =>
            SystemCatalog.ListAsync(transaction, BackupDestinationHistory.Kind(job.Database.ProjectId!), progress.DestinationAfter,
                16, BackupJsonContext.Default.ProjectBackupDestinationRecord, record => record.Revision, token), token).ConfigureAwait(false);
        foreach (var destination in destinations.Items)
        {
            failed |= !await AttemptAsync(job, destination.Revision, destination, prefix, progress.ObjectsAbsentAt, token).ConfigureAwait(false);
        }

        if (destinations.Next is not null)
        {
            return progress with { DestinationAfter = destinations.Next, InventoryFailed = failed, Failure = "inventory_pending" };
        }

        progress = progress with { DestinationAfter = null, InventoryFailed = false };
        if (failed)
        {
            return progress with { ObjectsAbsentAt = null, Failure = "objects_pending" };
        }

        if (progress.ObjectsAbsentAt is null)
        {
            return progress with { ObjectsAbsentAt = time.GetUtcNow(), Failure = "verification_pending" };
        }

        if (progress.KeysShreddedAt is null)
        {
            return progress with { Failure = "keys_pending" };
        }

        if (!await catalog.MarkBackupVerifiedAsync(job.Database, time.GetUtcNow(), token).ConfigureAwait(false))
        {
            return progress with { Failure = "authority_changed" };
        }

        return progress with { Failure = null };
    }

    private async Task<bool> AttemptAsync(DatabasePurgeRecord job, string destinationId, ProjectBackupDestinationRecord? destination,
        string prefix, DateTimeOffset? absentAt, CancellationToken token)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30), time);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token);
        var kind = "backup_purge_destinations/" + job.Database.DatabaseId;
        try
        {
            var cursor = await catalog.ReadUncheckedAsync(transaction => SystemCatalog.GetAsync(transaction, kind,
                destinationId, BackupPurgeJsonContext.Default.PrefixPurgeProgress, stop.Token), stop.Token).ConfigureAwait(false) ?? new PrefixPurgeProgress();
            using var customer = destination is null ? null : await targets.PurgeStoreAsync(destination, stop.Token).ConfigureAwait(false);
            var store = customer ?? platform;
            var result = await store.PurgePrefixAsync(prefix, cursor, stop.Token).ConfigureAwait(false);
            var saved = await catalog.WriteDurableAsync(async transaction =>
            {
                await SystemCatalog.PutAsync(transaction, kind, destinationId, result.Progress,
                    BackupPurgeJsonContext.Default.PrefixPurgeProgress, token).ConfigureAwait(false);
                return true;
            }, static _ => false, token).ConfigureAwait(false);
            if (!saved || !result.Absent)
            {
                return false;
            }

            return absentAt is null || !await store.SnapshotsEnabledAsync(stop.Token).ConfigureAwait(false)
                || !(await store.ListSnapshotsAsync(stop.Token).ConfigureAwait(false)).Any(snapshot => snapshot.At <= absentAt);
        }
        catch (Exception error) when (!token.IsCancellationRequested)
        {
            _failures.Add(1, new KeyValuePair<string, object?>("reason", "storage_unavailable"));
            var status = error switch
            {
                HttpRequestException { StatusCode: { } code } => (int)code,
                AmazonServiceException service => (int)service.StatusCode,
                _ => 0,
            };
            var bucket = destination is null ? "platform" : "customer";
            var reason = error is InvalidDataException ? error.Message : "";
            if (_pending.Report(job.Database.DatabaseId!, destinationId, $"{error.GetType().Name} {status} {reason}"))
            {
                logger.ZLogWarning($"Backup erasure of database {job.Database.DatabaseId:@Database} remains pending after a storage failure: {error.GetType().Name:@Error} {reason:@Reason}, HTTP status {status:@Status}, {bucket:@Bucket} bucket; every pass retries");
            }

            return false;
        }
    }

    private async Task SaveAsync(DatabasePurgeRecord job, BackupPurgeProgress progress, CancellationToken token)
    {
        _ = await catalog.WriteDurableAsync(async transaction =>
        {
            var current = await SystemCatalog.GetAsync(transaction, ProgressKind, job.Database.DatabaseId!,
                BackupPurgeJsonContext.Default.BackupPurgeProgress, token).ConfigureAwait(false);
            await SystemCatalog.PutAsync(transaction, ProgressKind, job.Database.DatabaseId!,
                progress with { KeysShreddedAt = progress.KeysShreddedAt ?? current?.KeysShreddedAt },
                BackupPurgeJsonContext.Default.BackupPurgeProgress, token).ConfigureAwait(false);
            return true;
        }, static _ => false, token).ConfigureAwait(false);
    }

    private sealed class PendingErasures
    {
        private readonly ConcurrentDictionary<string, DateTimeOffset> _requested = new(StringComparer.Ordinal);
        private readonly HashSet<string> _swept = [with(StringComparer.Ordinal)];
        private readonly Dictionary<(string Database, string Slot), string> _reported = [];

        public PendingErasures(Meter meter, TimeProvider time)
        {
            meter.CreateObservableGauge("terrabase.backup.purge_pending", () => _requested.Count,
                description: "Deleted databases whose backup erasure is not verified yet.");
            meter.CreateObservableGauge("terrabase.backup.purge_oldest_age", () =>
            {
                var now = time.GetUtcNow();
                return (now - _requested.Values.DefaultIfEmpty(now).Min()).TotalSeconds;
            }, "s", "Time since the oldest unverified backup erasure was requested.");
        }

        public void Pending(string database, DateTimeOffset requestedAt)
        {
            _requested[database] = requestedAt;
            _swept.Add(database);
        }

        public void Verified(string database)
        {
            _requested.TryRemove(database, out _);
            foreach (var key in _reported.Keys.Where(key => key.Database == database).ToArray())
            {
                _reported.Remove(key);
            }
        }

        public bool Report(string database, string slot, string cause)
        {
            if (_reported.TryGetValue((database, slot), out var last) && last == cause)
            {
                return false;
            }

            _reported[(database, slot)] = cause;
            return true;
        }

        public void Wrapped()
        {
            foreach (var database in _requested.Keys.Where(database => !_swept.Contains(database)))
            {
                Verified(database);
            }

            _swept.Clear();
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await _stopping.CancelAsync().ConfigureAwait(false);
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    public override void Dispose()
    {
        _stopping.Cancel();
        _stopping.Dispose();
        _pass.Dispose();
        base.Dispose();
    }
}

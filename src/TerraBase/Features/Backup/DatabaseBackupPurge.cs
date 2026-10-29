using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Amazon.Runtime;
using TerraBase.Features.Management;
using TerraBase.Features.Management.Backups;
using TerraBase.Hosting;
using ZLogger;

namespace TerraBase.Features.Backup;

internal sealed record BackupPurgeProgress(DateTimeOffset RequestedAt, string? DestinationAfter, DateTimeOffset? ObjectsAbsentAt,
    DateTimeOffset? LastAttemptAt, string? Failure, bool InventoryFailed = false);

[JsonSerializable(typeof(BackupPurgeProgress))]
[JsonSerializable(typeof(PrefixPurgeProgress))]
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
internal sealed partial class BackupPurgeJsonContext : JsonSerializerContext;

// Purge authority and credentials outlive the deleted database and project authorization.
// All holders must first acknowledge closure: otherwise a late upload could recreate an erased prefix.
internal sealed class DatabaseBackupPurge(
    SystemCatalog catalog, S3ObjectStore platform, BackupTargets targets, TimeProvider time,
    IMeterFactory meters, ILogger<DatabaseBackupPurge> logger) : BackgroundService
{
    internal const string ProgressCollection = "backup_purge_progress";
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

            foreach (var job in page.Items.Where(job => job.BackupVerifiedAt is null))
            {
                var progress = await catalog.ReadUncheckedAsync(transaction => SystemCatalog.GetAsync(transaction, ProgressCollection,
                    job.Database.DatabaseId!, BackupPurgeJsonContext.Default.BackupPurgeProgress, token), token).ConfigureAwait(false)
                    ?? new BackupPurgeProgress(job.RequestedAt, null, null, null, null);
                try
                {
                    progress = await PurgeAsync(job, progress, token).ConfigureAwait(false);
                }
                catch (Exception) when (!token.IsCancellationRequested)
                {
                    // Provider errors can contain credentials. Persist and log only a bounded classification.
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
                        // Names whom a closure waits on: only a holder's own receipt proves its copy gone.
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

    private async Task<BackupPurgeProgress> PurgeAsync(DatabasePurgeRecord job, BackupPurgeProgress progress, CancellationToken token)
    {
        if (!await catalog.AreActiveCopiesAbsentAsync(job.Database, token).ConfigureAwait(false))
        {
            return progress with { ObjectsAbsentAt = null, Failure = "active_copies_pending" };
        }

        var prefix = BackupLayout.FileRoot(job.Database) + "/";
        var failed = progress.InventoryFailed;
        failed |= !await AttemptAsync(job, BackupDestinationNames.Platform, null, prefix, progress.ObjectsAbsentAt, token).ConfigureAwait(false);
        var destinations = await catalog.ReadUncheckedAsync(transaction =>
            SystemCatalog.ListAsync(transaction, BackupDestinationHistory.Collection(job.Database.ProjectId!), progress.DestinationAfter,
                16, TerraBaseJsonContext.Default.ProjectBackupDestinationRecord, record => record.Revision, token), token).ConfigureAwait(false);
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
            // The next complete inventory verifies bucket copies against this immutable absence cut.
            return progress with { ObjectsAbsentAt = time.GetUtcNow(), Failure = "verification_pending" };
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
        // Each destination gets its own deadline and durable cursor; an inaccessible archived credential cannot starve others.
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30), time);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token);
        var collection = "backup_purge_destinations/" + job.Database.DatabaseId;
        try
        {
            var cursor = await catalog.ReadUncheckedAsync(transaction => SystemCatalog.GetAsync(transaction, collection,
                destinationId, BackupPurgeJsonContext.Default.PrefixPurgeProgress, stop.Token), stop.Token).ConfigureAwait(false) ?? new PrefixPurgeProgress();
            using var customer = destination is null ? null : await targets.PurgeStoreAsync(destination, stop.Token).ConfigureAwait(false);
            var store = customer ?? platform;
            var result = await store.PurgePrefixAsync(prefix, cursor, stop.Token).ConfigureAwait(false);
            var saved = await catalog.WriteDurableAsync(async transaction =>
            {
                await SystemCatalog.PutAsync(transaction, collection, destinationId, result.Progress,
                    BackupPurgeJsonContext.Default.PrefixPurgeProgress, token).ConfigureAwait(false);
                return true;
            }, static _ => false, token).ConfigureAwait(false);
            if (!saved || !result.Absent)
            {
                return false;
            }

            // Customer-owned bucket snapshots remain pending until their owner expires them.
            return absentAt is null || !await store.SnapshotsEnabledAsync(stop.Token).ConfigureAwait(false)
                || !(await store.ListSnapshotsAsync(stop.Token).ConfigureAwait(false)).Any(snapshot => snapshot.At <= absentAt);
        }
        catch (Exception error) when (!token.IsCancellationRequested)
        {
            _failures.Add(1, new KeyValuePair<string, object?>("reason", "storage_unavailable"));
            // Provider messages can hold credentials: only the error's type, HTTP status and bucket kind. Without them 31
            // erasures failed every pass for two days on every node with nothing saying why (2026-10-27).
            // One warning per erasure and cause, not per attempt: every pass retries, so the gauges show how long it stays stuck.
            var status = error switch
            {
                HttpRequestException { StatusCode: { } code } => (int)code,
                AmazonServiceException service => (int)service.StatusCode,
                _ => 0,
            };
            var bucket = destination is null ? "platform" : "customer";
            // TerraBase's own InvalidDataException messages are fixed text naming the failed validation.
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
            await SystemCatalog.PutAsync(transaction, ProgressCollection, job.Database.DatabaseId!, progress,
                BackupPurgeJsonContext.Default.BackupPurgeProgress, token).ConfigureAwait(false);
            return true;
        }, static _ => false, token).ConfigureAwait(false);
    }

    // Erasures still unverified at their last attempt, with their request times; written only under _pass.
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

        /// <summary>Whether this cause is new for the erasure's step (its inventory or one destination): worth one log line.</summary>
        public bool Report(string database, string slot, string cause)
        {
            if (_reported.TryGetValue((database, slot), out var last) && last == cause)
            {
                return false;
            }

            _reported[(database, slot)] = cause;
            return true;
        }

        // The purge listing wrapped: an erasure not seen since it started is no longer listed.
        public void Wrapped()
        {
            foreach (var database in _requested.Keys.Where(database => !_swept.Contains(database)))
            {
                Verified(database);
            }

            _swept.Clear();
        }
    }

    public override void Dispose()
    {
        _pass.Dispose();
        base.Dispose();
    }
}

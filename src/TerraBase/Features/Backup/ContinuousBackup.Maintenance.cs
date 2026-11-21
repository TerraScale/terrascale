using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using TerraBase.Engine;
using TerraBase.Errors;
using TerraBase.Features.Backup;
using TerraBase.Features.Management.Backups;
using TerraBase.Features.Management;
using TerraBase.Hosting;
using TerraBase.Tenancy;
using ZLogger;

namespace TerraBase.Features.Backup;

public sealed record BackupDrill(
    DateTimeOffset At, bool Passed, double RestoreSeconds, string? Error, string Region, string Member, string Destination,
    DateTimeOffset RecoveryPointAt, string Generation, long TargetPosition, long? ReachedPosition, string ExpectedDigest, string? RestoredDigest);

public sealed record BackupStatusRecord(
    string File, string Member, string? Generation, DateTimeOffset? LastShippedAt, double LagSeconds, long? SizeBytes, DateTimeOffset? SizedAt,
    int RetentionDays, BackupDrill? LastDrill, DateTimeOffset UpdatedAt);

internal sealed partial class ContinuousBackup
{
    private static readonly TimeSpan RetentionInterval = TimeSpan.FromHours(1);
    private static readonly TimeSpan SizeInterval = TimeSpan.FromDays(1);
    private static readonly TimeSpan DrillEvidenceRetention = TimeSpan.FromDays(14);
    private static readonly TimeSpan BucketSnapshotInterval = TimeSpan.FromDays(1);
    private static readonly TimeSpan BucketSnapshotRetention = TimeSpan.FromDays(14);
    private static readonly TimeSpan BaselineSlack = TimeSpan.FromDays(1);

    private static readonly TimeSpan RetentionGrace = TimeSpan.FromHours(1);

    internal static (string Kind, string Id) RecordKey(TenantContext? database) =>
        database is null ? ("backup_status", "_system") : ($"databases/{database.DatabaseId}/backup_status", "current");

    private async Task RetainAsync(ShippedFile file, CancellationToken cancellationToken)
    {
        var retained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (file.RetainGate)
        {
            file.Retaining = retained;
        }
        try
        {
            await RetainCoreAsync(file, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lock (file.RetainGate)
            {
                file.Retaining = null;
            }
            retained.TrySetResult();
        }
    }

    private async Task RetainCoreAsync(ShippedFile file, CancellationToken cancellationToken)
    {
        if (file.Database is { } database && await _catalog.ReadAsync<bool?>(async transaction =>
                await transaction.GetAsync(DeletedDatabases.Kind, database.DatabaseId!, cancellationToken).ConfigureAwait(false) is not null,
                static _ => null, cancellationToken).ConfigureAwait(false) is not false)
        {
            return;
        }

        var now = _time.GetUtcNow();
        if (file.Database is null)
        {
            file.RetentionDays = _settings.RetentionDays.Values.Max();
        }
        else
        {
            var plan = await _tenants.PlanAsync(file.Database.OrganizationId, cancellationToken).ConfigureAwait(false);
            if (plan is not PlanTier tier)
            {
                _ = plan switch
                {
                    Unavailable => new Done(),
                    PlanTier => throw new UnreachableException(),
                };
                return;
            }

            file.RetentionDays = _settings.RetentionDays[tier];
        }

        file.RetainedAt = now;
        var cutoff = now - TimeSpan.FromDays(file.RetentionDays) - RetentionGrace;
        var expires = now - TimeSpan.FromDays(file.RetentionDays) - BaselineSlack + RetentionInterval;
        var leased = await LeasedGenerationsAsync(file, now, cancellationToken).ConfigureAwait(false);
        if (leased is null)
        {
            _logger.ZLogWarning($"Retention of {file.Prefix:@File} could not read restore leases; it retries next hour");
            return;
        }

        long size = 0;
        var measure = now - file.SizedAt >= SizeInterval;
        var complete = true;
        foreach (var store in file.Target.RetentionStores)
        {
            try
            {
                if (!await RetainStoreAsync(file, store, cutoff, expires, leased, cancellationToken).ConfigureAwait(false))
                {
                    throw new IOException("backup expiry is incomplete; an object deletion was refused or failed");
                }

                if (measure)
                {
                    await foreach (var item in store.ListAsync(file.Prefix + "/", null, cancellationToken).ConfigureAwait(false))
                    {
                        size = checked(size + item.Size);
                    }
                }
            }
            catch (Exception error) when (!cancellationToken.IsCancellationRequested)
            {
                complete = false;
                _logger.ZLogWarning(error, $"Retention of {file.Prefix:@File} failed; it retries next hour");
            }
        }

        if (measure && complete)
        {
            (file.SizeBytes, file.SizedAt) = (size, now);
        }

        if (measure)
        {
            await _bucket.AbortStaleUploadsAsync(file.Prefix + "/", cancellationToken).ConfigureAwait(false);
        }

        if (file.Database is null)
        {
            await SnapshotBucketAsync(cancellationToken).ConfigureAwait(false);
        }

        await RecordAsync(file, cancellationToken).ConfigureAwait(false);
    }

    private async Task SnapshotBucketAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (_bucketSnapshotsEnabled is null)
            {
                _bucketSnapshotsEnabled = await _bucket.SnapshotsEnabledAsync(cancellationToken).ConfigureAwait(false);
                if (_bucketSnapshotsEnabled is false)
                {
                    _logger.ZLogInformation($"The backup bucket has no Tigris snapshots enabled; this process takes no bucket snapshots");
                }
            }

            if (_bucketSnapshotsEnabled is false)
            {
                return;
            }

            var now = _time.GetUtcNow();
            var snapshots = (await _bucket.ListSnapshotsAsync(cancellationToken).ConfigureAwait(false)).ToList();
            _newestBucketSnapshot = snapshots.Count == 0 ? null : snapshots.Max(snapshot => snapshot.At);
            foreach (var snapshot in snapshots.Where(snapshot => snapshot.At <= now - BucketSnapshotRetention + RetentionInterval).ToArray())
            {
                try
                {
                    await _bucket.DeleteSnapshotAsync(snapshot.Version, cancellationToken).ConfigureAwait(false);
                    snapshots.Remove(snapshot);
                    _newestBucketSnapshot = snapshots.Count == 0 ? null : snapshots.Max(item => item.At);
                    _bucketSnapshots.Add(1, new KeyValuePair<string, object?>("result", "deleted"));
                    _logger.ZLogInformation($"Deleted the backup bucket snapshot {snapshot.Version} taken {snapshot.At:O}");
                }
                catch (Exception error) when (!cancellationToken.IsCancellationRequested)
                {
                    _bucketSnapshots.Add(1, new KeyValuePair<string, object?>("result", "failed"));
                    _logger.ZLogWarning(error, $"Expiry of backup bucket snapshot {snapshot.Version} failed; it retries next hour");
                }
            }

            if (snapshots.Count == 0 || now - snapshots.Max(snapshot => snapshot.At) >= BucketSnapshotInterval)
            {
                await _bucket.TakeSnapshotAsync(cancellationToken).ConfigureAwait(false);
                _bucketSnapshots.Add(1, new KeyValuePair<string, object?>("result", "taken"));
                _logger.ZLogInformation($"Took the daily snapshot of the backup bucket");
                var taken = await _bucket.ListSnapshotsAsync(cancellationToken).ConfigureAwait(false);
                _newestBucketSnapshot = taken.Count == 0 ? null : taken.Max(snapshot => snapshot.At);
            }
        }
        catch (Exception error) when (!cancellationToken.IsCancellationRequested)
        {
            _bucketSnapshots.Add(1, new KeyValuePair<string, object?>("result", "failed"));
            _logger.ZLogWarning(error, $"Daily snapshot of the backup bucket failed; it retries next hour");
        }
    }

    private async ValueTask<HashSet<Ulid>?> LeasedGenerationsAsync(ShippedFile file, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (file.Database is not { } database)
        {
            return [];
        }

        return await _catalog.ReadAsync(
            async transaction => await RestoreGenerationLeases.ActiveAsync(
                transaction, database.OrganizationId, database.DatabaseId!, null, now, cancellationToken).ConfigureAwait(false),
            static _ => (HashSet<Ulid>?)null, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<bool> RetainStoreAsync(ShippedFile file, IObjectStore store, DateTimeOffset cutoff, DateTimeOffset expires, HashSet<Ulid> leased, CancellationToken cancellationToken)
    {
        var complete = true;
        var expiredKeys = new List<string>(16);
        await foreach (var item in store.ListAsync(file.Prefix + "/wal/", null, cancellationToken).ConfigureAwait(false))
        {
            if (BackupLayout.SegmentGeneration(item.Key) is not { } generation || leased.Contains(generation))
            {
                continue;
            }

            if (BackupLayout.ParseSegment(item.Key) is not { } segment || segment.At > expires)
            {
                continue;
            }

            expiredKeys.Add(item.Key);
            if (expiredKeys.Count == 16)
            {
                complete &= await DeleteRetainedAsync(file, store, expiredKeys, cancellationToken).ConfigureAwait(false);
                expiredKeys.Clear();
            }
        }

        complete &= await DeleteRetainedAsync(file, store, expiredKeys, cancellationToken).ConfigureAwait(false);
        expiredKeys.Clear();

        const int maxSnapshots = 10_000;
        var snapshots = new List<SnapshotKey>(maxSnapshots);
        SnapshotKey? oldest = null;
        await foreach (var item in store.ListAsync(BackupLayout.Snapshots(file.Prefix), null, cancellationToken).ConfigureAwait(false))
        {
            if (BackupLayout.ParseSnapshot(item.Key) is not { } snapshot || snapshot.At >= cutoff || leased.Contains(snapshot.Generation))
            {
                continue;
            }

            if (snapshot.At <= expires)
            {
                expiredKeys.Add(snapshot.Key);
                if (expiredKeys.Count == 16)
                {
                    complete &= await DeleteRetainedAsync(file, store, expiredKeys, cancellationToken).ConfigureAwait(false);
                    expiredKeys.Clear();
                }

                continue;
            }

            oldest = snapshot;
            if (snapshots.Count < maxSnapshots)
            {
                snapshots.Add(snapshot);
            }
        }

        complete &= await DeleteRetainedAsync(file, store, expiredKeys, cancellationToken).ConfigureAwait(false);

        if (oldest is null)
        {
            return complete && await RetainGenerationProofsAsync(store, file.Prefix, file.Generation, expires, cancellationToken).ConfigureAwait(false);
        }

        if (snapshots.Count > 0 && snapshots[^1].Key == oldest.Key)
        {
            snapshots.RemoveAt(snapshots.Count - 1);
        }

        var kept = snapshots.Select(snapshot => snapshot.Generation).Distinct().ToDictionary(generation => generation, _ => (long?)null);
        kept[oldest.Generation] = oldest.Position;
        var lastCandidate = snapshots.Count > 0 ? snapshots[^1].Key : null;
        await foreach (var item in store.ListAsync(BackupLayout.Snapshots(file.Prefix), lastCandidate, cancellationToken).ConfigureAwait(false))
        {
            if (BackupLayout.ParseSnapshot(item.Key) is { } snapshot && snapshot.At > expires && kept.TryGetValue(snapshot.Generation, out var position))
            {
                kept[snapshot.Generation] = position is { } frames ? Math.Min(frames, snapshot.Position) : snapshot.Position;
            }
        }

        foreach (var (generation, before) in kept)
        {
            if (leased.Contains(generation))
            {
                continue;
            }

            complete &= await DeleteSegmentsAsync(store, file, generation, before, cancellationToken).ConfigureAwait(false);
        }

        complete &= await DeleteRetainedAsync(file, store, [.. snapshots.Select(snapshot => snapshot.Key)], cancellationToken).ConfigureAwait(false);
        return complete && await RetainGenerationProofsAsync(store, file.Prefix, file.Generation, expires, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<bool> DeleteSegmentsAsync(IObjectStore store, ShippedFile file, Ulid generation, long? before, CancellationToken cancellationToken)
    {
        var complete = true;
        var batch = new List<string>(16);
        await foreach (var item in store.ListAsync(BackupLayout.Segments(file.Prefix, generation), null, cancellationToken).ConfigureAwait(false))
        {
            if (BackupLayout.ParseSegment(item.Key) is not { } segment)
            {
                continue;
            }

            if (segment.Start >= before)
            {
                break;
            }

            batch.Add(item.Key);
            if (batch.Count == 16)
            {
                complete &= await DeleteRetainedAsync(file, store, batch, cancellationToken).ConfigureAwait(false);

                batch.Clear();
            }
        }

        complete &= await DeleteRetainedAsync(file, store, batch, cancellationToken).ConfigureAwait(false);
        return complete;
    }

    private static async Task<bool> DeleteRetainedAsync(ShippedFile file, IObjectStore store, List<string> keys, CancellationToken cancellationToken)
    {
        if (keys.Count > 0)
        {
            SideWork? active;
            lock (file)
            {
                active = file.Side is { Drilling: true, Completion.IsCompleted: false } side ? side : null;
            }
            if (active is not null)
            {
                file.DrilledAt = DateTimeOffset.MinValue;
                active.Cancellation.Cancel();
                await active.Completion.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
        }
        return await DeleteAsync(store, keys, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<bool> DeleteAsync(IObjectStore store, List<string> keys, CancellationToken cancellationToken)
    {
        var complete = true;
        foreach (var chunk in keys.Chunk(16))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var deleted = await Task.WhenAll(chunk.Select(async key =>
            {
                try
                {
                    return await store.DeleteAsync(key, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception) when (!cancellationToken.IsCancellationRequested)
                {
                    return false;
                }
            })).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            complete &= deleted.All(done => done);
        }

        return complete;
    }

    private async Task DrillAsync(ShippedFile file, SnapshotKey stored, byte[] live, CancellationToken cancellationToken)
    {
        var started = _time.GetTimestamp();
        string? error = null;
        long? reached = null;
        string? digest = null;
        try
        {
            var source = stored;
            await foreach (var snapshot in file.Target.Reader.SnapshotsAsync(file.Prefix, cancellationToken).ConfigureAwait(false))
            {
                if (snapshot.Generation == stored.Generation && snapshot.Position < stored.Position)
                {
                    source = snapshot;
                }
            }

            var built = await file.Target.Reader.BuildAsync(file.Prefix, file.Scope, source, DateTimeOffset.MaxValue, stored.Position, next: null, cancellationToken)
                .ConfigureAwait(false);
            if (built is not RestoredBackup builtBackup)
            {
                error = built switch
                {
                    BackupHistoryIncomplete failure => failure.Reason,
                    RestorePointUnproven failure => failure.Reason,
                    BackupUnreadable failure => failure.Reason,
                    Unavailable failure => failure.Message,
                    RestoredBackup => throw new UnreachableException(),
                };
            }
            else
            {
                try
                {
                    reached = builtBackup.Position;
                    if (builtBackup.Position != stored.Position)
                    {
                        error = "the backup ends before the snapshot it is checked against";
                    }
                    else
                    {
                        var checksum = await _engine.ChecksumAsync(builtBackup.Path, cancellationToken).ConfigureAwait(false);
                        digest = Convert.ToHexString(checksum);
                        if (!live.SequenceEqual(checksum))
                        {
                            error = "the restored database differs from the live one";
                        }
                    }
                }
                finally
                {
                    builtBackup.Dispose();
                }
            }
        }
        catch (Exception failure) when (!cancellationToken.IsCancellationRequested)
        {
            error = failure.Message;
        }

        var drill = new BackupDrill(_time.GetUtcNow(), error is null, _time.GetElapsedTime(started).TotalSeconds, error,
            _region, _node, file.Target.Revision, stored.At, stored.Generation.ToString(), stored.Position, reached, Convert.ToHexString(live), digest);
        try
        {
            await RecordAsync(file, cancellationToken, drill).ConfigureAwait(false);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            file.LastDrill = drill with { Passed = false, Error = "the restore drill result could not be committed" };
            _drills.Add(1, new KeyValuePair<string, object?>("result", "failed"));
            _logger.ZLogError($"The restore drill record of {file.Prefix:@File} could not be committed");
            throw;
        }
        _drills.Add(1, new KeyValuePair<string, object?>("result", drill.Passed ? "passed" : "failed"));
        if (!drill.Passed)
        {
            _logger.ZLogError($"The restore drill of {file.Prefix:@File} failed: {error!:@Reason}");
        }
    }

    private async Task RecordAsync(ShippedFile file, CancellationToken cancellationToken, BackupDrill? drill = null)
    {
        await file.RecordGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var record = new BackupStatusRecord(file.Prefix, _node, file.Generation?.ToString(), file.LastShipped, Lag(file),
                file.SizeBytes, file.SizedAt == DateTimeOffset.MinValue ? null : file.SizedAt, file.RetentionDays, drill ?? file.LastDrill, _time.GetUtcNow());
            var (kind, id) = RecordKey(file.Database);
            var store = await _storage.OpenSystemDatabaseAsync(cancellationToken).ConfigureAwait(false);
            var transaction = await store.BeginTransactionAsync(write: true, cancellationToken).ConfigureAwait(false);
            await using (transaction.ConfigureAwait(false))
            {
                await transaction.PutAsync(kind, id, JsonSerializer.SerializeToUtf8Bytes(record, BackupJsonContext.Default.BackupStatusRecord), cancellationToken)
                    .ConfigureAwait(false);
                if (drill is not null)
                {
                    var history = file.Database is null ? "backup_drills" : $"databases/{file.Database.DatabaseId}/backup_drills";
                    var evidence = $"{_region}/{_node}/{drill.Destination}/";
                    await transaction.PutAsync(history, evidence + EvidenceDate(drill.At),
                        JsonSerializer.SerializeToUtf8Bytes(drill, BackupJsonContext.Default.BackupDrill), cancellationToken).ConfigureAwait(false);
                    await PruneDrillEvidenceAsync(transaction, history, evidence, evidence + EvidenceDate(drill.At - DrillEvidenceRetention), cancellationToken)
                        .ConfigureAwait(false);
                }
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                if (drill is not null)
                {
                    file.LastDrill = drill;
                }
            }
        }
        catch (Exception error) when (drill is null && !cancellationToken.IsCancellationRequested)
        {
            _logger.ZLogWarning(error, $"The backup record of {file.Prefix:@File} could not be written; the next pass retries");
        }
        finally
        {
            file.RecordGate.Release();
        }
    }

    private static string EvidenceDate(DateTimeOffset at) => at.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static async Task PruneDrillEvidenceAsync(IStorageTransaction transaction, string history, string evidence, string oldestKept,
        CancellationToken cancellationToken)
    {
        var after = evidence;
        while (true)
        {
            var page = await transaction.ListAsync(history, after, 32, 1 << 20, cancellationToken).ConfigureAwait(false);
            var expired = page.Records.TakeWhile(record => record.Id.StartsWith(evidence, StringComparison.Ordinal)
                && string.CompareOrdinal(record.Id, oldestKept) < 0).ToList();
            foreach (var record in expired)
            {
                await transaction.DeleteAsync(history, record.Id, cancellationToken).ConfigureAwait(false);
            }
            if (expired.Count < page.Records.Count || !page.HasMore)
            {
                return;
            }
            after = expired[^1].Id;
        }
    }
}

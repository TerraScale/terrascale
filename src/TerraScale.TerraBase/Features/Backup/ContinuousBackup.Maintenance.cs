using System.Text.Json;
using TerraBase.Engine;
using TerraBase.Hosting;
using TerraBase.Tenancy;

namespace TerraBase.Features.Backup;

/// <summary>A restore drill: the file rebuilt from the bucket compared with the live file at the same frame position.</summary>
/// <param name="At">When it ran.</param>
/// <param name="Passed">Whether the rebuilt file's checksum equals the live file's.</param>
/// <param name="RestoreSeconds">Seconds the download, roll-forward and integrity check took.</param>
/// <param name="Error">Why it failed; null when it passed.</param>
public sealed record BackupDrill(DateTimeOffset At, bool Passed, double RestoreSeconds, string? Error);

/// <summary>A file's backup as stored in TerraBase's own catalog, refreshed hourly and after each drill.</summary>
/// <param name="File">The file's directory in the bucket.</param>
/// <param name="Member">The member that ships it.</param>
/// <param name="Generation">The generation being shipped.</param>
/// <param name="LastShippedAt">When the newest data in the bucket was read.</param>
/// <param name="LagSeconds">Seconds the oldest data not yet in the bucket has waited.</param>
/// <param name="SizeBytes">Bytes the file's backup occupies in the bucket, measured daily.</param>
/// <param name="SizedAt">When <paramref name="SizeBytes"/> was measured.</param>
/// <param name="RetentionDays">The retention of the database's plan.</param>
/// <param name="LastDrill">The last restore drill.</param>
/// <param name="UpdatedAt">When this record was written.</param>
public sealed record BackupStatusRecord(
    string File, string Member, string? Generation, DateTimeOffset? LastShippedAt, double LagSeconds, long? SizeBytes, DateTimeOffset? SizedAt,
    int RetentionDays, BackupDrill? LastDrill, DateTimeOffset UpdatedAt);

/// <summary>Retention by plan, daily restore drills and the backup record of each shipped file.</summary>
internal sealed partial class ContinuousBackup
{
    private static readonly TimeSpan RetentionInterval = TimeSpan.FromHours(1);
    private static readonly TimeSpan DrillInterval = TimeSpan.FromDays(1);
    private static readonly TimeSpan SizeInterval = TimeSpan.FromDays(1);

    // Kept past the window, so a restore that picked a snapshot just inside it finishes before retention deletes it.
    private static readonly TimeSpan RetentionGrace = TimeSpan.FromHours(1);

    /// <summary>The collection of a database's backup record in the system catalog, deleted with the database's state.</summary>
    internal static (string Collection, string Id) RecordKey(TenantContext? database) =>
        database is null ? ("backup_status", "_system") : ($"databases/{database.DatabaseId}/backup_status", "current");

    // Deletes what no restore inside the plan's window (plus the grace) needs: snapshots older than the newest one before
    // the window, the segments before that one in its generation, and generations with no snapshot left.
    private async Task RetainAsync(ShippedFile file, CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow();
        file.RetainedAt = now;
        file.RetentionDays = file.Database is null
            ? _settings.RetentionDays.Values.Max()
            : _settings.RetentionDays[await _tenants.PlanAsync(file.Database.OrganizationId, cancellationToken).ConfigureAwait(false)];
        var cutoff = now - TimeSpan.FromDays(file.RetentionDays) - RetentionGrace;
        var snapshots = new List<SnapshotKey>();
        await foreach (var snapshot in _reader.SnapshotsAsync(file.Prefix, cancellationToken).ConfigureAwait(false))
        {
            snapshots.Add(snapshot);
        }

        var floor = snapshots.FindLastIndex(snapshot => snapshot.At < cutoff);
        if (floor > 0)
        {
            var kept = snapshots[floor..].Select(snapshot => snapshot.Generation).ToHashSet();
            var expired = new List<string>();
            foreach (var generation in snapshots[..floor].Select(snapshot => snapshot.Generation).Distinct().Where(generation => !kept.Contains(generation)))
            {
                await foreach (var item in _store.ListAsync(BackupLayout.Segments(file.Prefix, generation), null, cancellationToken).ConfigureAwait(false))
                {
                    expired.Add(item.Key);
                }
            }

            var oldest = snapshots[floor];
            await foreach (var item in _store.ListAsync(BackupLayout.Segments(file.Prefix, oldest.Generation), null, cancellationToken).ConfigureAwait(false))
            {
                if (BackupLayout.ParseSegment(item.Key) is not { } segment || segment.Start >= oldest.Position)
                {
                    break;
                }

                expired.Add(item.Key);
            }

            // Segments first: an interrupted pass leaves old snapshots whose segments are gone, outside the window, and the
            // next pass deletes them; never segments that no snapshot reaches.
            await DeleteAsync(expired, cancellationToken).ConfigureAwait(false);
            await DeleteAsync([.. snapshots[..floor].Select(snapshot => snapshot.Key)], cancellationToken).ConfigureAwait(false);
            LogRetained(_logger, file.Prefix, floor, expired.Count);
        }

        if (now - file.SizedAt >= SizeInterval)
        {
            long size = 0;
            await foreach (var item in _store.ListAsync(file.Prefix + "/", null, cancellationToken).ConfigureAwait(false))
            {
                size += item.Size;
            }

            (file.SizeBytes, file.SizedAt) = (size, now);
        }

        await RecordAsync(file, cancellationToken).ConfigureAwait(false);
    }

    private async Task DeleteAsync(List<string> keys, CancellationToken cancellationToken) =>
        await Parallel.ForEachAsync(keys, new ParallelOptions { MaxDegreeOfParallelism = 16, CancellationToken = cancellationToken },
            async (key, token) => await _store.DeleteAsync(key, token).ConfigureAwait(false)).ConfigureAwait(false);

    // Rebuilds the file from the bucket up to the snapshot just stored, from the generation's previous snapshot when it has
    // one, and compares it with that snapshot's local copy: the live file at the same frame position.
    // ponytail: runs in the file's shipping pass, so that file ships again only after the drill; move drills off the pass
    // if large files make them delay shipping.
    private async Task DrillAsync(ShippedFile file, SnapshotKey stored, string local, CancellationToken cancellationToken)
    {
        var started = _time.GetTimestamp();
        string? error = null;
        try
        {
            var source = stored;
            await foreach (var snapshot in _reader.SnapshotsAsync(file.Prefix, cancellationToken).ConfigureAwait(false))
            {
                if (snapshot.Generation == stored.Generation && snapshot.Position < stored.Position)
                {
                    source = snapshot;
                }
            }

            var (restored, reached) = await _reader.BuildAsync(file.Prefix, file.Scope, source, DateTimeOffset.MaxValue, stored.Position, cancellationToken)
                .ConfigureAwait(false);
            try
            {
                if (reached != stored.Position)
                {
                    error = "the backup ends before the snapshot it is checked against";
                }
                else
                {
                    var live = await _engine.ChecksumAsync(local, cancellationToken).ConfigureAwait(false);
                    var rebuilt = await _engine.ChecksumAsync(restored, cancellationToken).ConfigureAwait(false);
                    if (!live.AsSpan().SequenceEqual(rebuilt))
                    {
                        error = "the restored database differs from the live one";
                    }
                }
            }
            finally
            {
                BackupReader.Delete(restored);
            }
        }
        catch (Exception failure) when (!cancellationToken.IsCancellationRequested)
        {
            error = failure.Message;
        }

        var drill = new BackupDrill(_time.GetUtcNow(), error is null, _time.GetElapsedTime(started).TotalSeconds, error);
        (file.LastDrill, file.DrilledAt) = (drill, drill.At);
        _drills.Add(1, new KeyValuePair<string, object?>("result", drill.Passed ? "passed" : "failed"));
        if (!drill.Passed)
        {
            LogDrillFailed(_logger, file.Prefix, error!);
        }

        await RecordAsync(file, cancellationToken).ConfigureAwait(false);
    }

    // Writes the file's backup record into the system catalog; a member that cannot write the catalog skips it.
    private async Task RecordAsync(ShippedFile file, CancellationToken cancellationToken)
    {
        var record = new BackupStatusRecord(file.Prefix, _node, file.Generation?.ToString(), file.LastShipped, Lag(file, _time.GetUtcNow()),
            file.SizeBytes, file.SizedAt == DateTimeOffset.MinValue ? null : file.SizedAt, file.RetentionDays, file.LastDrill, _time.GetUtcNow());
        var (collection, id) = RecordKey(file.Database);
        try
        {
            var store = await _storage.OpenSystemDatabaseAsync(cancellationToken).ConfigureAwait(false);
            var transaction = await store.BeginTransactionAsync(write: true, cancellationToken).ConfigureAwait(false);
            await using (transaction.ConfigureAwait(false))
            {
                await transaction.PutAsync(collection, id, JsonSerializer.SerializeToUtf8Bytes(record, TerraBaseJsonContext.Default.BackupStatusRecord), cancellationToken)
                    .ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (CatalogFollowerException)
        {
            // anna-full (TS-647): every member writes _system once it is a lattice database; a catalog follower skips it.
        }
        catch (Exception error) when (!cancellationToken.IsCancellationRequested)
        {
            LogRecordFailed(_logger, file.Prefix, error);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Retention removed {Snapshots} snapshots and {Segments} segments of {File}")]
    private static partial void LogRetained(ILogger logger, string file, int snapshots, int segments);

    [LoggerMessage(Level = LogLevel.Error, Message = "The restore drill of {File} failed: {Reason}")]
    private static partial void LogDrillFailed(ILogger logger, string file, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The backup record of {File} could not be written; the next pass retries")]
    private static partial void LogRecordFailed(ILogger logger, string file, Exception error);
}

using Amazon.S3;
using TerraBase.Configuration;
using TerraBase.Engine;
using TerraBase.Errors;
using TerraBase.Tenancy;

namespace TerraBase.Features.Backup;

/// <summary>The times a database can be restored to.</summary>
/// <param name="Earliest">Its oldest snapshot, limited by the plan's retention window.</param>
/// <param name="Latest">The newest data in the bucket.</param>
public sealed record RestoreWindow(DateTimeOffset Earliest, DateTimeOffset Latest);

/// <summary>Advisory backup metadata; integrity and segment contiguity are checked only during restore.</summary>
/// <param name="Window">The database's retention window and newest backed-up data.</param>
/// <param name="PointInTime">The requested restore time.</param>
/// <param name="SnapshotAt">When the selected snapshot was taken.</param>
/// <param name="LatestSegmentAt">The newest applicable segment time, or null when only the snapshot applies.</param>
/// <param name="SegmentCount">Segments of the selected snapshot's generation applicable to the requested time.</param>
public sealed record PointInTimePlan(RestoreWindow Window, DateTimeOffset PointInTime, DateTimeOffset SnapshotAt, DateTimeOffset? LatestSegmentAt, int SegmentCount);

/// <summary>A read-only restore plan, or why its metadata could not be read.</summary>
public readonly union PointInTimePlanResult(PointInTimePlan, NotFound, Validation, Unavailable);

/// <summary>A completed restore, or why its requested time could not be restored.</summary>
public readonly union PointInTimeRestoreResult(Done, NotFound, Validation);

/// <summary>Point-in-time restore from continuous backup (TS-633).</summary>
public interface IPointInTimeRestore
{
    /// <summary>The restore window of <paramref name="database"/>; <see langword="null"/> when no backup remains within its plan's retention window.</summary>
    Task<RestoreWindow?> GetWindowAsync(TenantContext database, CancellationToken cancellationToken, string destination = "platform");

    /// <summary>Reads bounded backup metadata without downloading, decrypting or importing any backup.</summary>
    Task<PointInTimePlanResult> PlanAsync(TenantContext database, DateTimeOffset at, CancellationToken cancellationToken, string destination = "platform");

    /// <summary>
    /// Creates <paramref name="target"/>, a new engine database, holding <paramref name="source"/> as of <paramref name="at"/>:
    /// every transaction the backup read by then. Validates its restore window at entry, before downloading or importing.
    /// </summary>
    /// <exception cref="InvalidDataException">The backup is incomplete or fails authentication or its integrity check.</exception>
    Task<PointInTimeRestoreResult> RestoreAsync(TenantContext source, DateTimeOffset at, TenantContext target, CancellationToken cancellationToken, string destination = "platform");
}

/// <summary>Restores from the newest snapshot at or before the time, rolled forward through its generation's segments.</summary>
internal sealed class PointInTimeRestore(
    IWalBackup engine, BackupTargets targets, IContinuousBackup shipper, ITenantDirectory tenants, BackupSettings settings, TimeProvider time)
    : IPointInTimeRestore
{
    private const int MaxPlanItems = 10_000;
    private static readonly TimeSpan PlanTimeout = TimeSpan.FromSeconds(10);

    public async Task<PointInTimePlanResult> PlanAsync(TenantContext database, DateTimeOffset at, CancellationToken cancellationToken, string destination = "platform")
    {
        var now = time.GetUtcNow();
        using var timeout = new CancellationTokenSource(PlanTimeout, time);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            using var backup = await targets.RestoreAsync(database, destination, deadline.Token).ConfigureAwait(false);
            if (backup is null)
            {
                return new NotFound("continuous backup holds nothing of this database");
            }

            var file = BackupLayout.File(database);
            var snapshots = await ScanSnapshotsAsync(backup.Store, file, at, MaxPlanItems, deadline.Token).ConfigureAwait(false);
            if (snapshots is null)
            {
                return PlanLimit();
            }

            if (snapshots.First is not { } first || snapshots.Last is not { } last)
            {
                return new NotFound("continuous backup holds nothing of this database");
            }

            var earliest = await EarliestAsync(database, first.At, now, deadline.Token).ConfigureAwait(false);
            var remaining = MaxPlanItems - snapshots.Scanned;
            var latest = destination == "platform" ? shipper.LastShipped(database, last.Generation) : null;
            SegmentScan? segments = null;
            if (latest is null)
            {
                var newest = await ScanSegmentsAsync(backup.Store, file, last, at, remaining, stopAfterTarget: false, deadline.Token).ConfigureAwait(false);
                if (newest is null)
                {
                    return PlanLimit();
                }

                latest = newest.Newest ?? last.At;
                remaining -= newest.Scanned;
                if (snapshots.Chosen == last)
                {
                    segments = newest;
                }
            }

            var storedLatest = latest.Value > last.At ? latest.Value : last.At;
            if (earliest > storedLatest)
            {
                return new NotFound("continuous backup holds nothing of this database within its plan's retention window");
            }

            if (at < earliest || at > storedLatest)
            {
                return new Validation("pointInTime must lie between the restore window's earliest and latest times");
            }

            if (snapshots.Chosen is not { } chosen)
            {
                return new NotFound("continuous backup holds nothing of this database");
            }

            segments ??= await ScanSegmentsAsync(backup.Store, file, chosen, at, remaining, stopAfterTarget: true, deadline.Token).ConfigureAwait(false);
            return segments is null
                ? PlanLimit()
                : new PointInTimePlan(new(earliest, storedLatest), at, chosen.At, segments.Latest, segments.Count);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return new Unavailable("the point-in-time plan metadata scan timed out", "pitr_plan_timeout");
        }
        catch (Exception error) when (error is AmazonS3Exception or HttpRequestException or IOException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return timeout.IsCancellationRequested
                ? new Unavailable("the point-in-time plan metadata scan timed out", "pitr_plan_timeout")
                : new Unavailable("the point-in-time plan metadata could not be read", "pitr_plan_unavailable");
        }
    }

    public async Task<RestoreWindow?> GetWindowAsync(TenantContext database, CancellationToken cancellationToken, string destination = "platform")
    {
        var now = time.GetUtcNow();
        using var backup = await targets.RestoreAsync(database, destination, cancellationToken).ConfigureAwait(false);
        if (backup is null)
        {
            return null;
        }

        var file = BackupLayout.File(database);
        var snapshots = await ScanSnapshotsAsync(backup.Store, file, DateTimeOffset.MaxValue, int.MaxValue, cancellationToken).ConfigureAwait(false);
        return await WindowAsync(database, file, snapshots, now, cancellationToken, backup, destination).ConfigureAwait(false);
    }

    private async Task<RestoreWindow?> WindowAsync(TenantContext database, string file, SnapshotScan? snapshots, DateTimeOffset now, CancellationToken cancellationToken, BackupTarget backup, string destination)
    {
        if (snapshots?.First is not { } first || snapshots.Last is not { } last)
        {
            return null;
        }

        // The shipper knows its own progress; otherwise the newest segment after the newest snapshot tells.
        var shipped = destination == "platform" ? shipper.LastShipped(database, last.Generation) : null;
        var latest = shipped ?? last.At;
        if (shipped is null)
        {
            await foreach (var item in backup.Store.ListAsync(BackupLayout.Segments(file, last.Generation),
                BackupLayout.SegmentsBefore(file, last.Generation, last.Position), cancellationToken).ConfigureAwait(false))
            {
                if (BackupLayout.ParseSegment(item.Key) is { } segment && segment.At > latest)
                {
                    latest = segment.At;
                }
            }
        }

        var earliest = await EarliestAsync(database, first.At, now, cancellationToken).ConfigureAwait(false);
        latest = latest > last.At ? latest : last.At;
        return earliest <= latest ? new(earliest, latest) : null;
    }

    public async Task<PointInTimeRestoreResult> RestoreAsync(TenantContext source, DateTimeOffset at, TenantContext target, CancellationToken cancellationToken, string destination = "platform")
    {
        // Pin retention before metadata awaits; the same scan discovers the window and selects the snapshot.
        var now = time.GetUtcNow();
        using var backup = await targets.RestoreAsync(source, destination, cancellationToken).ConfigureAwait(false);
        if (backup is null)
        {
            return new NotFound("continuous backup holds nothing of this database");
        }

        var file = BackupLayout.File(source);
        var snapshots = await ScanSnapshotsAsync(backup.Store, file, at, int.MaxValue, cancellationToken).ConfigureAwait(false);
        if (snapshots?.First is null)
        {
            return new NotFound("continuous backup holds nothing of this database");
        }

        if (await WindowAsync(source, file, snapshots, now, cancellationToken, backup, destination).ConfigureAwait(false) is not { } window)
        {
            return new NotFound("continuous backup holds nothing of this database within its plan's retention window");
        }

        if (at < window.Earliest || at > window.Latest)
        {
            return new Validation("pointInTime must lie between the restore window's earliest and latest times");
        }

        if (snapshots?.Chosen is not { } chosen)
        {
            return new NotFound("continuous backup holds nothing of this database");
        }

        var (restored, _) = await backup.Reader.BuildAsync(file, BackupLayout.KeyScope(source), chosen, at, until: null, cancellationToken).ConfigureAwait(false);
        try
        {
            await engine.ImportDatabaseAsync(target, restored, cancellationToken).ConfigureAwait(false);
            return new Done();
        }
        finally
        {
            BackupReader.Delete(restored);
        }
    }

    private static Unavailable PlanLimit() => new("the point-in-time plan metadata scan exceeds 10000 objects", "pitr_plan_limit");

    private async Task<DateTimeOffset> EarliestAsync(TenantContext database, DateTimeOffset snapshotAt, DateTimeOffset now, CancellationToken cancellationToken)
    {
        // The plan's window, not the bucket's: retention keeps a predecessor snapshot, and object lock may keep more.
        var floor = now - TimeSpan.FromDays(settings.RetentionDays[await tenants.PlanAsync(database.OrganizationId, cancellationToken).ConfigureAwait(false)]);
        return snapshotAt > floor ? snapshotAt : floor;
    }

    // Scan raw objects so malformed metadata also consumes the preview budget. Both restore and preview select here.
    private async Task<SnapshotScan?> ScanSnapshotsAsync(IObjectStore store, string file, DateTimeOffset at, int limit, CancellationToken cancellationToken)
    {
        SnapshotKey? first = null;
        SnapshotKey? last = null;
        SnapshotKey? chosen = null;
        var scanned = 0;
        await foreach (var item in store.ListAsync(BackupLayout.Snapshots(file), null, cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++scanned > limit)
            {
                return null;
            }

            if (BackupLayout.ParseSnapshot(item.Key) is not { } snapshot)
            {
                continue;
            }

            first ??= snapshot;
            last = snapshot;
            if (snapshot.At <= at)
            {
                chosen = snapshot;
            }
        }

        return new(first, last, chosen, scanned);
    }

    private async Task<SegmentScan?> ScanSegmentsAsync(IObjectStore store, string file, SnapshotKey snapshot, DateTimeOffset at, int limit, bool stopAfterTarget, CancellationToken cancellationToken)
    {
        DateTimeOffset? latest = null;
        DateTimeOffset? newest = null;
        var pastTarget = false;
        var count = 0;
        var scanned = 0;
        await foreach (var item in store.ListAsync(BackupLayout.Segments(file, snapshot.Generation),
            BackupLayout.SegmentsBefore(file, snapshot.Generation, snapshot.Position), cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++scanned > limit)
            {
                return null;
            }

            if (BackupLayout.ParseSegment(item.Key) is not { } segment)
            {
                continue;
            }

            newest = newest is null || segment.At > newest ? segment.At : newest;
            if (segment.At > at)
            {
                pastTarget = true;
                if (stopAfterTarget)
                {
                    break;
                }

                continue;
            }

            // Restore stops at the first later segment even when subsequent wall-clock timestamps regress.
            if (pastTarget)
            {
                continue;
            }

            count++;
            latest = latest is null || segment.At > latest ? segment.At : latest;
        }

        return new(latest, newest, count, scanned);
    }

    private sealed record SnapshotScan(SnapshotKey? First, SnapshotKey? Last, SnapshotKey? Chosen, int Scanned);

    private sealed record SegmentScan(DateTimeOffset? Latest, DateTimeOffset? Newest, int Count, int Scanned);
}

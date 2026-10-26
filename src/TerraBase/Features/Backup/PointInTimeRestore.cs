using Amazon.S3;
using TerraBase.Configuration;
using TerraBase.Engine;
using TerraBase.Errors;
using TerraBase.Features.Platform;
using TerraBase.Tenancy;

namespace TerraBase.Features.Backup;

/// <summary>The times a database can be restored to.</summary>
/// <param name="Earliest">Its oldest snapshot, limited by the plan's retention window.</param>
/// <param name="Latest">
/// The newest time the backup proves restorable: the second at or before the start of the newest backup read marked in
/// one of its newest segments, or its newest snapshot's time.
/// </param>
public sealed record RestoreWindow(DateTimeOffset Earliest, DateTimeOffset Latest);

/// <summary>Advisory backup metadata, read from object names only; restore also checks contents and every segment's length.</summary>
/// <param name="Window">The database's retention window and newest backed-up data.</param>
/// <param name="PointInTime">The requested restore time.</param>
/// <param name="SnapshotAt">When the selected snapshot was taken.</param>
/// <param name="LatestSegmentAt">The newest applicable segment time, or null when only the snapshot applies.</param>
/// <param name="SegmentCount">Segments of the selected snapshot's generation applicable to the requested time.</param>
/// <param name="HistoryCheck">
/// How far the history up to the time was checked: always <c>metadata</c>, the first segment's start, the segments' order
/// and the history's end, from object names. Whether each segment begins where the previous one ends needs their lengths,
/// which only a restore reads, so a restore can still answer 409 <c>backup_history_incomplete</c>.
/// </param>
public sealed record PointInTimePlan(
    RestoreWindow Window, DateTimeOffset PointInTime, DateTimeOffset SnapshotAt, DateTimeOffset? LatestSegmentAt, int SegmentCount, string HistoryCheck);

/// <summary>A read-only restore plan, or why its metadata could not be read or shows the history incomplete.</summary>
public readonly union PointInTimePlanResult(PointInTimePlan, NotFound, Conflict, Validation, Unavailable);

/// <summary>A completed restore, or why its requested time could not be restored.</summary>
public readonly union PointInTimeRestoreResult(Done, NotFound, Conflict, Validation, Unavailable);

/// <summary>A restore window, no backup, or an unreadable plan.</summary>
public readonly union RestoreWindowResult(RestoreWindow, NotFound, Unavailable);

/// <summary>Point-in-time restore from continuous backup (TS-633).</summary>
public interface IPointInTimeRestore
{
    /// <summary>The restore window, <see cref="NotFound"/> when no backup remains within its plan's retention window, or <see cref="Unavailable"/> when its plan cannot be read.</summary>
    Task<RestoreWindowResult> GetWindowAsync(TenantContext database, CancellationToken cancellationToken, string destination = BackupDestinationNames.Platform);

    /// <summary>Reads bounded backup metadata without downloading, decrypting or importing any backup.</summary>
    Task<PointInTimePlanResult> PlanAsync(TenantContext database, DateTimeOffset at, CancellationToken cancellationToken, string destination = BackupDestinationNames.Platform);

    /// <summary>Reads an isolated, caller-owned historical snapshot without publishing a tenant database.</summary>
    Task<HistoricalSnapshotResult> ReadSnapshotAsync(TenantContext source, DateTimeOffset at, CancellationToken cancellationToken, string destination = BackupDestinationNames.Platform);

    /// <summary>
    /// Creates <paramref name="target"/>, a new engine database, holding <paramref name="source"/> as of <paramref name="at"/>:
    /// every transaction committed before then and none recorded as committed at or after it, whole. Validates its restore
    /// window at entry, before downloading or importing.
    /// Answers <see cref="Conflict"/> <c>backup_history_incomplete</c> unless the backup proves the history complete up to the time,
    /// <c>restore_point_unproven</c> when it cannot place the time between two transactions, and <c>backup_unreadable</c> when
    /// an object it needs fails authentication or the restored file its integrity check.
    /// </summary>
    Task<PointInTimeRestoreResult> RestoreAsync(TenantContext source, DateTimeOffset at, TenantContext target, CancellationToken cancellationToken, string destination = BackupDestinationNames.Platform);
}

/// <summary>Restores from the newest snapshot at or before the time, rolled forward through its generation's segments.</summary>
internal sealed class PointInTimeRestore(
    IWalBackup engine, IRecoverySnapshotReader scratch, BackupTargets targets, IContinuousBackup shipper, ITenantDirectory tenants, BackupSettings settings, TimeProvider time)
    : IPointInTimeRestore
{
    private const int MaxPlanItems = 10_000;

    // Segments, newest first, whose ends a window reads for a read mark.
    private const int MarkReads = 3;
    private static readonly TimeSpan PlanTimeout = TimeSpan.FromSeconds(10);

    public async Task<PointInTimePlanResult> PlanAsync(TenantContext database, DateTimeOffset at, CancellationToken cancellationToken, string destination = BackupDestinationNames.Platform)
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

            var retained = await EarliestAsync(database, first.At, now, deadline.Token).ConfigureAwait(false);
            if (retained is not DateTimeOffset earliest)
            {
                return retained switch
                {
                    Unavailable error => error,
                    DateTimeOffset => throw new System.Diagnostics.UnreachableException(),
                };
            }

            var remaining = MaxPlanItems - snapshots.Scanned;
            var latest = destination == BackupDestinationNames.Platform ? shipper.LastShipped(database, last.Generation) : null;
            SegmentScan? segments = null;
            SegmentScan? newest = null;
            if (latest is null)
            {
                // Its verdict counts only when the chosen snapshot is the last one, which has no next snapshot.
                newest = await ScanSegmentsAsync(backup.Store, file, last, snapshots.Next, at, remaining, stopAfterTarget: false, deadline.Token).ConfigureAwait(false);
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
                return await OutsideAsync(database, at, now, new(earliest, storedLatest), deadline.Token).ConfigureAwait(false) switch
                {
                    Validation error => error,
                    Conflict error => error,
                    Unavailable error => error,
                };
            }

            if (snapshots.Chosen is not { } chosen)
            {
                return new NotFound("continuous backup holds nothing of this database");
            }

            segments ??= await ScanSegmentsAsync(backup.Store, file, chosen, snapshots.Next, at, remaining, stopAfterTarget: true, deadline.Token).ConfigureAwait(false);
            if (segments is not { Proven: true })
            {
                return segments is null ? PlanLimit() : HistoryIncomplete();
            }

            // Read from the bucket, the window ends at the newest time a read mark proves; the shipper's own progress stands.
            var proven = newest is null ? storedLatest
                : await ProvenLatestAsync(backup, BackupLayout.KeyScope(database), last, newest.NewestKeys, deadline.Token).ConfigureAwait(false);
            return new PointInTimePlan(new(earliest, proven), at, chosen.At, segments.Latest, segments.Count, "metadata");
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return new Unavailable("the point-in-time plan metadata scan timed out", "pitr_plan_timeout");
        }
        catch (Exception error) when (error is AmazonS3Exception or HttpRequestException or IOException or InvalidDataException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return timeout.IsCancellationRequested
                ? new Unavailable("the point-in-time plan metadata scan timed out", "pitr_plan_timeout")
                : new Unavailable("the point-in-time plan metadata could not be read", "pitr_plan_unavailable");
        }
    }

    public async Task<RestoreWindowResult> GetWindowAsync(TenantContext database, CancellationToken cancellationToken, string destination = BackupDestinationNames.Platform)
    {
        var now = time.GetUtcNow();
        using var backup = await targets.RestoreAsync(database, destination, cancellationToken).ConfigureAwait(false);
        if (backup is null)
        {
            return new NotFound();
        }

        var file = BackupLayout.File(database);
        var snapshots = await ScanSnapshotsAsync(backup.Store, file, DateTimeOffset.MaxValue, int.MaxValue, cancellationToken).ConfigureAwait(false);
        return await WindowAsync(database, file, snapshots, now, backup, destination, proven: true, cancellationToken).ConfigureAwait(false);
    }

    // Latest is the newest data, or with proven, the newest time a read mark proves when it comes from the bucket.
    private async Task<RestoreWindowResult> WindowAsync(
        TenantContext database, string file, SnapshotScan? snapshots, DateTimeOffset now, BackupTarget backup, string destination, bool proven, CancellationToken cancellationToken)
    {
        if (snapshots?.First is not { } first || snapshots.Last is not { } last)
        {
            return new NotFound();
        }

        // The shipper knows its own progress; otherwise the newest segment after the newest snapshot tells.
        var shipped = destination == BackupDestinationNames.Platform ? shipper.LastShipped(database, last.Generation) : null;
        var latest = shipped ?? last.At;
        if (shipped is null)
        {
            var newest = new Queue<string>(MarkReads + 1);
            await foreach (var item in backup.Store.ListAsync(BackupLayout.Segments(file, last.Generation),
                BackupLayout.SegmentsBefore(file, last.Generation, last.Position), cancellationToken).ConfigureAwait(false))
            {
                if (BackupLayout.ParseSegment(item.Key) is not { } segment)
                {
                    continue;
                }

                latest = segment.At > latest ? segment.At : latest;
                newest.Enqueue(segment.Key);
                if (newest.Count > MarkReads)
                {
                    newest.Dequeue();
                }
            }

            if (proven)
            {
                latest = await ProvenLatestAsync(backup, BackupLayout.KeyScope(database), last, [.. newest], cancellationToken).ConfigureAwait(false);
            }
        }

        var retained = await EarliestAsync(database, first.At, now, cancellationToken).ConfigureAwait(false);
        if (retained is not DateTimeOffset earliest)
        {
            return retained switch
            {
                Unavailable error => error,
                DateTimeOffset => throw new System.Diagnostics.UnreachableException(),
            };
        }

        latest = latest > last.At ? latest : last.At;
        return earliest <= latest ? new RestoreWindow(earliest, latest) : new NotFound();
    }

    public async Task<PointInTimeRestoreResult> RestoreAsync(TenantContext source, DateTimeOffset at, TenantContext target, CancellationToken cancellationToken, string destination = BackupDestinationNames.Platform)
    {
        var built = await BuildSnapshotAsync(source, at, destination, cancellationToken).ConfigureAwait(false);
        if (built is not RestoredBackup restored)
        {
            return built switch
            {
                NotFound error => error,
                Conflict error => error,
                Validation error => error,
                Unavailable error => error,
                RestoredBackup => throw new System.Diagnostics.UnreachableException(),
            };
        }
        try
        {
            await engine.ImportDatabaseAsync(target, restored.Path, cancellationToken).ConfigureAwait(false);
            return new Done();
        }
        finally
        {
            restored.Dispose();
        }
    }

    public async Task<HistoricalSnapshotResult> ReadSnapshotAsync(TenantContext source, DateTimeOffset at, CancellationToken cancellationToken, string destination = BackupDestinationNames.Platform)
    {
        var built = await BuildSnapshotAsync(source, at, destination, cancellationToken).ConfigureAwait(false);
        if (built is not RestoredBackup restored)
        {
            return built switch
            {
                NotFound error => error,
                Conflict error => error,
                Validation error => error,
                Unavailable error => error,
                RestoredBackup => throw new System.Diagnostics.UnreachableException(),
            };
        }
        try
        {
            return new HistoricalSnapshot(await scratch.OpenAsync(restored.Path, source, cancellationToken).ConfigureAwait(false), restored);
        }
        catch
        {
            restored.Dispose();
            throw;
        }
    }

    private readonly union SnapshotFileResult(RestoredBackup, NotFound, Conflict, Validation, Unavailable);

    private async Task<SnapshotFileResult> BuildSnapshotAsync(TenantContext source, DateTimeOffset at, string destination, CancellationToken cancellationToken)
    {
        // Every caller must preserve the canonical erasure boundary, even before any receipt exists.
        if (PlatformDatabases.IsPlatform(source.OrganizationId))
        {
            return new Conflict("platform database historical recovery is fenced by erasure requirements", "recovery_erasure_fenced");
        }

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

        var found = await WindowAsync(source, file, snapshots, now, backup, destination, proven: false, cancellationToken).ConfigureAwait(false);
        if (found is not RestoreWindow window)
        {
            return found switch
            {
                NotFound => new NotFound("continuous backup holds nothing of this database within its plan's retention window"),
                Unavailable error => error,
                RestoreWindow => throw new System.Diagnostics.UnreachableException(),
            };
        }

        if (at < window.Earliest || at > window.Latest)
        {
            return await OutsideAsync(source, at, now, window, cancellationToken).ConfigureAwait(false) switch
            {
                Validation error => error,
                Conflict error => error,
                Unavailable error => error,
            };
        }

        if (snapshots?.Chosen is not { } chosen)
        {
            return new NotFound("continuous backup holds nothing of this database");
        }

        // Object names alone refuse most incomplete histories before anything downloads; the replay checks the rest.
        if (await ScanSegmentsAsync(backup.Store, file, chosen, snapshots.Next, at, int.MaxValue, stopAfterTarget: true, cancellationToken)
            .ConfigureAwait(false) is not { Proven: true })
        {
            return HistoryIncomplete();
        }

        RestoredBackup restored;
        try
        {
            restored = await backup.Reader.BuildAsync(file, BackupLayout.KeyScope(source), chosen, at, until: null, snapshots.Next, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (BackupHistoryIncompleteException)
        {
            return HistoryIncomplete();
        }
        catch (BackupInventoryLimitException error)
        {
            return new Unavailable(error.Message, "backup_inventory_limit");
        }
        catch (RestorePointUnprovenException error)
        {
            return new Conflict("continuous backup cannot place pointInTime between two transactions: " + error.Message, "restore_point_unproven");
        }
        catch (InvalidDataException)
        {
            return new Conflict("continuous backup of this database cannot be read: an object fails authentication or its integrity check", "backup_unreadable");
        }

        return restored;
    }

    private static Unavailable PlanLimit() => new("the point-in-time plan metadata scan exceeds 10000 objects", "pitr_plan_limit");

    private readonly union OutsideResult(Validation, Conflict, Unavailable);

    // A time outside the window, for restore and plan alike: invalid only when no backup can ever hold it (after now, or
    // before the plan's retention window); otherwise the backup does not hold it yet or any more (an unshipped or lost
    // final segment, a lost snapshot), a retryable 409.
    private async Task<OutsideResult> OutsideAsync(TenantContext database, DateTimeOffset at, DateTimeOffset now, RestoreWindow window, CancellationToken cancellationToken) =>
        at > now ? OutsideWindow(window) : await EarliestAsync(database, DateTimeOffset.MinValue, now, cancellationToken).ConfigureAwait(false) switch
        {
            DateTimeOffset retained when at >= retained => HistoryIncomplete(),
            Unavailable error => error,
            DateTimeOffset => OutsideWindow(window),
        };

    private static Validation OutsideWindow(RestoreWindow window) => new(string.Create(System.Globalization.CultureInfo.InvariantCulture,
        $"pointInTime must lie between the restore window's earliest and latest times: {window.Earliest:O} to {window.Latest:O}"));

    private static Conflict HistoryIncomplete() =>
        new("continuous backup cannot prove this database's history complete up to pointInTime", "backup_history_incomplete");

    /// <summary>
    /// Whether an object read at <paramref name="readAt"/> covers <paramref name="at"/>: the same millisecond or earlier,
    /// the precision of object names.
    /// </summary>
    internal static bool Covers(DateTimeOffset readAt, DateTimeOffset at) => at.ToUnixTimeMilliseconds() <= readAt.ToUnixTimeMilliseconds();

    private readonly union EarliestResult(DateTimeOffset, Unavailable);

    private async Task<EarliestResult> EarliestAsync(TenantContext database, DateTimeOffset snapshotAt, DateTimeOffset now, CancellationToken cancellationToken)
    {
        // The plan's window, not the bucket's: retention keeps a predecessor snapshot, and object lock may keep more.
        return await tenants.PlanAsync(database.OrganizationId, cancellationToken).ConfigureAwait(false) switch
        {
            PlanTier tier => snapshotAt > now - TimeSpan.FromDays(settings.RetentionDays[tier])
                ? snapshotAt : now - TimeSpan.FromDays(settings.RetentionDays[tier]),
            Unavailable error => error,
        };
    }

    // Scan raw objects so malformed metadata also consumes the preview budget. Both restore and preview select here.
    private static async Task<SnapshotScan?> ScanSnapshotsAsync(IObjectStore store, string file, DateTimeOffset at, int limit, CancellationToken cancellationToken)
    {
        SnapshotKey? first = null;
        SnapshotKey? last = null;
        SnapshotKey? chosen = null;
        SnapshotKey? next = null;
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
                (chosen, next) = (snapshot, null);
            }
            else if (next is null && snapshot.Generation == chosen?.Generation)
            {
                next = snapshot;
            }
        }

        return new(first, last, chosen, next, scanned);
    }

    // The selection restore makes, from object names: segments up to the time, stopping at the first later one even when
    // subsequent wall-clock timestamps regress. Proven unless the names already show the history incomplete up to the time:
    // the first segment must start at the snapshot's position and each later one at or after the previous (one holding no
    // log ends where it starts; the replay checks lengths); then either the first later segment follows them (the replay
    // places the time in it), the snapshot was taken at the time, the last one was read at the time (only a read mark at
    // its end can then prove it, which the replay checks), or the generation's next snapshot is not before them.
    // A segment's own time is when its read finished, not a point every earlier commit had reached, so it proves nothing.
    private static async Task<SegmentScan?> ScanSegmentsAsync(
        IObjectStore store, string file, SnapshotKey snapshot, SnapshotKey? next, DateTimeOffset at, int limit, bool stopAfterTarget, CancellationToken cancellationToken)
    {
        DateTimeOffset? latest = null;
        DateTimeOffset? newest = null;
        var newestKeys = new Queue<string>(MarkReads + 1);
        SegmentKey? last = null;
        bool? bounded = null;
        var ordered = true;
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
            newestKeys.Enqueue(segment.Key);
            if (newestKeys.Count > MarkReads)
            {
                newestKeys.Dequeue();
            }

            if (bounded is not null)
            {
                continue;
            }

            var follows = last is null ? segment.Start == snapshot.Position : segment.Start >= last.Start;
            if (segment.At > at)
            {
                bounded = follows;
                if (stopAfterTarget)
                {
                    break;
                }

                continue;
            }

            ordered &= follows;
            count++;
            latest = latest is null || segment.At > latest ? segment.At : latest;
            last = segment;
        }

        var proven = ordered && (bounded == true || Covers(last?.At ?? snapshot.At, at)
            || next is not null && (last is null ? next.Position == snapshot.Position : next.Position >= last.Start));
        return new(latest, newest, count, scanned, proven, [.. newestKeys]);
    }

    // The newest time the backup proves restorable from last, the newest snapshot, and newest, its generation's newest
    // segment keys (oldest first): the second at or before the start of the newest read marked at the end of a readable
    // one of them, where every transaction is placed either side of a whole second, or last's own time.
    private static async Task<DateTimeOffset> ProvenLatestAsync(
        BackupTarget backup, string scope, SnapshotKey last, IReadOnlyList<string> newest, CancellationToken cancellationToken)
    {
        for (var index = newest.Count - 1; index >= 0; index--)
        {
            DateTimeOffset? mark;
            try
            {
                mark = await backup.Reader.ReadMarkAsync(newest[index], scope, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error) when (error is BackupHistoryIncompleteException or InvalidDataException)
            {
                // Gone since the listing (retention), or unreadable (corrupted, cut short): it proves nothing, as if missing.
                continue;
            }

            if (mark is { } started)
            {
                var second = DateTimeOffset.FromUnixTimeSeconds(started.ToUnixTimeSeconds());
                return second > last.At ? second : last.At;
            }
        }

        return last.At;
    }

    // Next: the first snapshot after Chosen of the same generation.
    private sealed record SnapshotScan(SnapshotKey? First, SnapshotKey? Last, SnapshotKey? Chosen, SnapshotKey? Next, int Scanned);

    // NewestKeys: the generation's last MarkReads segment keys scanned, oldest first.
    private sealed record SegmentScan(DateTimeOffset? Latest, DateTimeOffset? Newest, int Count, int Scanned, bool Proven, IReadOnlyList<string> NewestKeys);
}

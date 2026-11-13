using System.Diagnostics;
using System.Globalization;
using Amazon.Runtime;
using Amazon.S3;
using TerraBase.Configuration;
using TerraBase.Engine;
using TerraBase.Errors;
using TerraBase.Features.Platform;
using TerraBase.Tenancy;

namespace TerraBase.Features.Backup;

public sealed record RestoreWindow(DateTimeOffset Earliest, DateTimeOffset Latest);

public sealed record PointInTimePlan(
    RestoreWindow Window, DateTimeOffset PointInTime, DateTimeOffset SnapshotAt, DateTimeOffset? LatestSegmentAt, int SegmentCount, string HistoryCheck,
    Ulid? Generation = null);

public readonly union PointInTimePlanResult(PointInTimePlan, NotFound, Conflict, Validation, Unavailable);

public readonly union PointInTimeRestoreResult(Done, NotFound, Conflict, Validation, Unavailable);

public readonly union RestoreWindowResult(RestoreWindow, NotFound, Unavailable);

public interface IPointInTimeRestore
{
    Task<RestoreWindowResult> GetWindowAsync(TenantContext database, CancellationToken cancellationToken, string destination = BackupDestinationNames.Platform);

    Task<PointInTimePlanResult> PlanAsync(TenantContext database, DateTimeOffset at, CancellationToken cancellationToken, string destination = BackupDestinationNames.Platform);

    Task<HistoricalSnapshotResult> ReadSnapshotAsync(TenantContext source, DateTimeOffset at, CancellationToken cancellationToken, string destination = BackupDestinationNames.Platform);

    Task<PointInTimeRestoreResult> RestoreAsync(TenantContext source, DateTimeOffset at, TenantContext target, CancellationToken cancellationToken, string destination = BackupDestinationNames.Platform);
}

internal sealed class PointInTimeRestore(
    IWalBackup engine, IRecoverySnapshotReader scratch, BackupTargets targets, IContinuousBackup shipper, ITenantDirectory tenants, BackupSettings settings, TimeProvider time, TerraBaseOptions options)
    : IPointInTimeRestore
{
    private const int MaxPlanItems = 10_000;

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

            if (await LostAsync(database, at, backup, deadline.Token).ConfigureAwait(false) is { } lost)
            {
                return lost;
            }

            var file = BackupLayout.File(database, options.MachineId);
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
                    DateTimeOffset => throw new UnreachableException(),
                };
            }

            var remaining = MaxPlanItems - snapshots.Scanned;
            var latest = destination == BackupDestinationNames.Platform ? shipper.LastShipped(database, last.Generation) : null;
            SegmentScan? segments = null;
            SegmentScan? newest = null;
            if (latest is null)
            {
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

            var proven = newest is null ? storedLatest
                : await ProvenLatestAsync(backup, BackupLayout.KeyScope(database), last, newest.NewestKeys, deadline.Token).ConfigureAwait(false);
            return new PointInTimePlan(new(earliest, proven), at, chosen.At, segments.Latest, segments.Count, "metadata", chosen.Generation);
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

        var file = BackupLayout.File(database, options.MachineId);
        var snapshots = await ScanSnapshotsAsync(backup.Store, file, DateTimeOffset.MaxValue, MaxPlanItems, cancellationToken).ConfigureAwait(false);
        if (snapshots is null)
        {
            return PlanLimit();
        }

        return await WindowAsync(database, file, snapshots, now, backup, destination, proven: true, cancellationToken).ConfigureAwait(false);
    }

    private async Task<RestoreWindowResult> WindowAsync(
        TenantContext database, string file, SnapshotScan? snapshots, DateTimeOffset now, BackupTarget backup, string destination, bool proven, CancellationToken cancellationToken)
    {
        if (snapshots?.First is not { } first || snapshots.Last is not { } last)
        {
            return new NotFound();
        }

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
                DateTimeOffset => throw new UnreachableException(),
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
                RestoredBackup => throw new UnreachableException(),
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
                RestoredBackup => throw new UnreachableException(),
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
        if (PlatformDatabases.IsPlatform(source.OrganizationId))
        {
            return new Conflict("platform database historical recovery is fenced by erasure requirements", "recovery_erasure_fenced");
        }

        var now = time.GetUtcNow();
        using var backup = await targets.RestoreAsync(source, destination, cancellationToken).ConfigureAwait(false);
        if (backup is null)
        {
            return new NotFound("continuous backup holds nothing of this database");
        }

        if (await LostAsync(source, at, backup, cancellationToken).ConfigureAwait(false) is { } lost)
        {
            return lost;
        }

        if (await backup.Keys.ShreddedAsync(BackupLayout.KeyScope(source), cancellationToken).ConfigureAwait(false) is { } shredded)
        {
            return Shredded(shredded.ShreddedAt);
        }

        var file = BackupLayout.File(source, options.MachineId);
        var snapshots = await ScanSnapshotsAsync(backup.Store, file, at, MaxPlanItems, cancellationToken).ConfigureAwait(false);
        if (snapshots is null)
        {
            return PlanLimit();
        }

        if (snapshots.First is null)
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
                RestoreWindow => throw new UnreachableException(),
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

        if (snapshots.Chosen is not { } chosen)
        {
            return new NotFound("continuous backup holds nothing of this database");
        }

        var segments = await ScanSegmentsAsync(backup.Store, file, chosen, snapshots.Next, at, MaxPlanItems, stopAfterTarget: true, cancellationToken)
            .ConfigureAwait(false);
        if (segments is not { Proven: true })
        {
            return segments is null ? PlanLimit() : HistoryIncomplete();
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
        catch (DatabaseShreddedException error)
        {
            return Shredded(error.ShreddedAt);
        }
        catch (InvalidDataException)
        {
            return new Conflict("continuous backup of this database cannot be read: an object fails authentication or its integrity check", "backup_unreadable");
        }

        return restored;
    }

    private static Conflict Shredded(DateTimeOffset? at) => new(
        at is { } when && when > DateTimeOffset.MinValue
            ? $"the database was deleted at {when:O} and its backup keys destroyed; its backups cannot be restored"
            : "the database was deleted and its backup keys destroyed; its backups cannot be restored",
        "database_shredded");

    private static Unavailable PlanLimit() => new($"the point-in-time metadata scan exceeds {MaxPlanItems} objects", "pitr_plan_limit");

    private readonly union OutsideResult(Validation, Conflict, Unavailable);

    private async Task<OutsideResult> OutsideAsync(TenantContext database, DateTimeOffset at, DateTimeOffset now, RestoreWindow window, CancellationToken cancellationToken) =>
        at > now ? OutsideWindow(window) : await EarliestAsync(database, DateTimeOffset.MinValue, now, cancellationToken).ConfigureAwait(false) switch
        {
            DateTimeOffset retained when at >= retained => HistoryIncomplete(),
            Unavailable error => error,
            DateTimeOffset => OutsideWindow(window),
        };

    private static Validation OutsideWindow(RestoreWindow window) => new(string.Create(CultureInfo.InvariantCulture,
        $"pointInTime must lie between the restore window's earliest and latest times: {window.Earliest:O} to {window.Latest:O}"));

    private static Conflict HistoryIncomplete() =>
        new("continuous backup cannot prove this database's history complete up to pointInTime", "backup_history_incomplete");

    private async Task<Conflict?> LostAsync(TenantContext database, DateTimeOffset at, BackupTarget backup, CancellationToken cancellationToken)
    {
        var local = await shipper.HolesAsync(database, cancellationToken).ConfigureAwait(false);
        if (local.UnavailableBefore is { } floor && at < floor)
        {
            return Lost(null, floor, open: false);
        }

        if (Latest(local.Lost, at) is { } known)
        {
            return known;
        }

        List<BackupHole> recorded = [];
        try
        {
            await foreach (var item in backup.Store.ListAsync(BackupLayout.Holes(BackupLayout.File(database, options.MachineId)), null, cancellationToken)
                .ConfigureAwait(false))
            {
                recorded.Add(BackupLayout.ParseHole(item.Key) ?? throw new InvalidDataException("a lost-history record of the backup has an unreadable name"));
            }
        }
        catch (Exception error) when (error is AmazonServiceException or AmazonClientException or HttpRequestException or IOException or InvalidDataException && !cancellationToken.IsCancellationRequested)
        {
            return new Conflict("continuous backup's record of lost history could not be read, so its history cannot be shown complete up to pointInTime",
                "backup_history_incomplete", new HistoryGap(null, null));
        }

        return Latest(recorded.Merged(), at);
    }

    private static Conflict? Latest(IReadOnlyList<BackupHole> holes, DateTimeOffset at) =>
        holes.Where(hole => hole.Covers(at)).OrderBy(hole => hole.FromAt ?? DateTimeOffset.MinValue).LastOrDefault() is { } hole
            ? Lost(hole.FromAt, hole.ToAt, hole.Open)
            : null;

    private static Conflict Lost(DateTimeOffset? from, DateTimeOffset? to, bool open) => new(string.Create(CultureInfo.InvariantCulture,
            $"continuous backup cannot prove this database's history complete up to pointInTime: its history from {Bound(from)} to {Bound(to)} was lost before it was stored")
        + (open ? ", and no recovery point after it is stored yet" : ""),
        "backup_history_incomplete", new HistoryGap(from, to));

    private static string Bound(DateTimeOffset? at) => at is { } known ? known.ToString("O", CultureInfo.InvariantCulture) : "an unknown time";

    internal static bool Covers(DateTimeOffset readAt, DateTimeOffset at) => at.ToUnixTimeMilliseconds() <= readAt.ToUnixTimeMilliseconds();

    private readonly union EarliestResult(DateTimeOffset, Unavailable);

    private async Task<EarliestResult> EarliestAsync(TenantContext database, DateTimeOffset snapshotAt, DateTimeOffset now, CancellationToken cancellationToken)
    {
        return await tenants.PlanAsync(database.OrganizationId, cancellationToken).ConfigureAwait(false) switch
        {
            PlanTier tier => snapshotAt > now - TimeSpan.FromDays(settings.RetentionDays[tier])
                ? snapshotAt : now - TimeSpan.FromDays(settings.RetentionDays[tier]),
            Unavailable error => error,
        };
    }

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

    private sealed record SnapshotScan(SnapshotKey? First, SnapshotKey? Last, SnapshotKey? Chosen, SnapshotKey? Next, int Scanned);

    private sealed record SegmentScan(DateTimeOffset? Latest, DateTimeOffset? Newest, int Count, int Scanned, bool Proven, IReadOnlyList<string> NewestKeys);
}

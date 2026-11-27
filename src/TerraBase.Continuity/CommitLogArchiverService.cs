using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace TerraBase.Continuity;

internal sealed record ArchiveCheckState(CausalBookmark ArchivedThrough, DateTimeOffset NextVisitUtc);

internal sealed class ArchiveActivity
{
    private readonly ConcurrentDictionary<Ulid, CommitWatermark> _observed = new();

    public void Observe(Ulid databaseId, string origin, long sequence) =>
        _observed.GetOrAdd(databaseId, static _ => new CommitWatermark()).Advance(origin, sequence);

    public bool IsCovered(Ulid databaseId, CausalBookmark archived) =>
        !_observed.TryGetValue(databaseId, out var watermark) || watermark.IsCoveredBy(archived);

    public void Forget(Ulid databaseId) => _observed.TryRemove(databaseId, out _);

    private sealed class CommitWatermark
    {
        private readonly Lock _gate = new();
        private readonly Dictionary<string, long> _through = new(StringComparer.Ordinal);

        public void Advance(string origin, long sequence)
        {
            lock (_gate)
            {
                ref var current = ref CollectionsMarshal.GetValueRefOrAddDefault(_through, origin, out _);
                if (sequence > current)
                {
                    current = sequence;
                }
            }
        }

        public bool IsCoveredBy(CausalBookmark archived)
        {
            lock (_gate)
            {
                foreach (var (origin, sequence) in _through)
                {
                    if (archived.Get(origin) < sequence)
                    {
                        return false;
                    }
                }

                return true;
            }
        }
    }
}

public sealed class CommitLogArchiverService(
    BackupContinuityService continuity,
    IContinuityDatabaseCatalog catalog,
    ChangefeedHub changefeed,
    ContinuityOptions options,
    ContinuityMetrics metrics,
    TimeProvider timeProvider,
    ILogger<CommitLogArchiverService> logger) : BackgroundService
{
    private static readonly TimeSpan RetentionSweepInterval = TimeSpan.FromHours(1);

    private readonly ConcurrentDictionary<Ulid, ArchiveCheckState> _checks = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!continuity.IsBackupEnabled)
        {
            ContinuityLog.BackupDisabled(logger);
            return;
        }

        using var timer = new PeriodicTimer(options.ArchiveInterval, timeProvider);
        do
        {
            await PassAsync(stoppingToken);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task PassAsync(CancellationToken stoppingToken)
    {
        IReadOnlyList<Ulid> databases;
        try
        {
            databases = await catalog.ListActiveDatabasesAsync(stoppingToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            ContinuityLog.ArchivePassFaulted(logger, exception);
            return;
        }

        ForgetInactive(databases);
        var now = timeProvider.GetUtcNow();
        List<Ulid> due = [];
        for (var index = 0; index < databases.Count; index++)
        {
            if (IsDue(databases[index], now))
            {
                due.Add(databases[index]);
            }
        }

        await Parallel.ForEachAsync(
            due,
            new ParallelOptions { MaxDegreeOfParallelism = options.ArchiveParallelism, CancellationToken = stoppingToken },
            ArchiveAsync);
    }

    private bool IsDue(Ulid databaseId, DateTimeOffset now) =>
        !_checks.TryGetValue(databaseId, out var check)
        || now >= check.NextVisitUtc
        || !changefeed.Activity.IsCovered(databaseId, check.ArchivedThrough);

    private void ForgetInactive(IReadOnlyList<Ulid> databases)
    {
        if (_checks.IsEmpty)
        {
            return;
        }

        HashSet<Ulid> active = [.. databases];
        foreach (var databaseId in _checks.Keys)
        {
            if (!active.Contains(databaseId))
            {
                _ = _checks.TryRemove(databaseId, out _);
                changefeed.Activity.Forget(databaseId);
            }
        }
    }

    private async ValueTask ArchiveAsync(Ulid databaseId, CancellationToken stoppingToken)
    {
        try
        {
            var archived = await continuity.ArchiveAsync(databaseId, forceSnapshot: false, stoppingToken);
            var failure = archived switch
            {
                BackupStatus status => await CheckedAsync(databaseId, status, stoppingToken),
                ContinuityNotFound => Idle(databaseId),
                ContinuityUnavailable unavailable => unavailable.Message,
            };
            if (failure is not null)
            {
                _ = _checks.TryRemove(databaseId, out _);
                metrics.ArchiveFailed();
                ContinuityLog.ArchiveFailed(logger, databaseId, failure);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _ = _checks.TryRemove(databaseId, out _);
            metrics.ArchiveFailed();
            ContinuityLog.ArchiveFaulted(logger, exception, databaseId);
        }
    }

    private async ValueTask<string?> CheckedAsync(Ulid databaseId, BackupStatus status, CancellationToken cancellationToken)
    {
        if (_checks.TryGetValue(databaseId, out var previous) && !status.ArchivedThrough.Dominates(previous.ArchivedThrough))
        {
            changefeed.Activity.Forget(databaseId);
        }

        var now = timeProvider.GetUtcNow();
        var sweep = now + RetentionSweepInterval;
        var interval = await catalog.IsPreferredArchiverAsync(databaseId, cancellationToken)
            ? options.SnapshotInterval
            : options.SnapshotInterval + options.SnapshotFailoverGrace;
        var snapshotDue = status.LastSnapshotAtUtc + interval;
        var next = status.PendingSegments > 0
            ? now
            : snapshotDue > now && snapshotDue < sweep ? snapshotDue : sweep;
        _checks[databaseId] = new ArchiveCheckState(status.ArchivedThrough, next);
        return null;
    }

    private string? Idle(Ulid databaseId)
    {
        _checks[databaseId] = new ArchiveCheckState(CausalBookmark.Empty, timeProvider.GetUtcNow() + RetentionSweepInterval);
        return null;
    }
}

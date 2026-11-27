using ZLogger;

namespace TerraScale.Platform.Features.FileStorage;

public sealed class ProjectFileSweepWorker(
    PlatformStore store,
    ProjectFileErasure erasure,
    PlatformOptions options,
    PlatformReadiness readiness,
    TimeProvider timeProvider,
    ILogger<ProjectFileSweepWorker> logger) : BackgroundService
{
    private const int PageSize = 100;
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan GracePeriod = FileStorageService.UploadLifetime + FileStorageService.UploadSettle;
    private static readonly TimeSpan FastGraceWindow = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan[] GraceBackoff =
    [
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(15),
        TimeSpan.FromSeconds(60),
        TimeSpan.FromMinutes(5),
    ];
    private static readonly TimeSpan IdleInterval = TimeSpan.FromMinutes(1);
    private readonly Dictionary<string, GraceSweepState> graceSweeps = new(StringComparer.Ordinal);
    private DateTimeOffset rowsDueAt;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, timeProvider);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            if (!readiness.SchemaReady)
            {
                continue;
            }

            try
            {
                await TickAsync(stoppingToken);
            }
            catch (Exception error) when (!stoppingToken.IsCancellationRequested && error is not OutOfMemoryException)
            {
                logger.ZLogWarning(error, $"Closed project file sweep failed");
            }
        }
    }

    private async Task TickAsync(CancellationToken cancellationToken)
    {
        await SweepRowsAsync(cancellationToken);
        if (options.Files is not null)
        {
            await SweepAsync(cancellationToken);
        }
    }

    private async Task SweepRowsAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        if (now < rowsDueAt)
        {
            return;
        }

        var outcome = await erasure.SweepClosedRowsAsync(cancellationToken);
        rowsDueAt = outcome switch
        {
            ClosedFileRowsSwept => now,
            ClosedFileRowsIdle => now + IdleInterval,
            PlatformUnavailable unavailable => RowsDeferred(unavailable.Message, now + IdleInterval),
        };
    }

    private DateTimeOffset RowsDeferred(string message, DateTimeOffset retryAt)
    {
        logger.ZLogWarning($"Closed project file row sweep deferred: {message}");
        return retryAt;
    }

    private async Task SweepAsync(CancellationToken cancellationToken)
    {
        var known = await store.ReadAsync(SqlValues.Statement(ProjectFileErasure.RegionsQuery), cancellationToken);
        var regions = known switch
        {
            SqlExecuteResponse { Results: var results } => ProjectFileErasure.Regions(results[0]),
            PlatformConflict conflict => Deferred<string>(conflict.Message),
            PlatformUnavailable unavailable => Deferred<string>(unavailable.Message),
        };
        if (regions.Count == 0)
        {
            return;
        }

        var graceMs = (long)GracePeriod.TotalMilliseconds;
        var fastGraceMs = (long)FastGraceWindow.TotalMilliseconds;
        var sweptAt = timeProvider.GetUtcNow().UnixMs();
        var cutoff = sweptAt - graceMs;
        var after = string.Empty;
        HashSet<string> seen = new(StringComparer.Ordinal);
        while (true)
        {
            var page = await store.ReadAsync(SqlValues.Statement(
                "SELECT project_id, organization_id, closed_at FROM platform_closed_file_projects WHERE functions_erased > 0 AND (swept_at IS NULL OR swept_at < closed_at + ?) AND project_id > ? ORDER BY project_id LIMIT ?",
                SqlValues.Number(graceMs), SqlValues.Text(after), SqlValues.Number(PageSize)), cancellationToken);
            IReadOnlyList<SqlRow>? projects = page switch
            {
                SqlExecuteResponse { Results: var results } => results[0].RowsOf().ToArray(),
                PlatformConflict conflict => DeferredRows(conflict.Message),
                PlatformUnavailable unavailable => DeferredRows(unavailable.Message),
            };
            if (projects is null)
            {
                return;
            }

            foreach (var row in projects)
            {
                after = row.Text(0);
                seen.Add(after);
                var hasState = graceSweeps.TryGetValue(after, out var state);
                if (hasState && sweptAt < state.NextSweepMs)
                {
                    continue;
                }

                var erased = await erasure.SweepAsync(Ulid.Parse(after), Ulid.Parse(row.Text(1)), regions, cancellationToken);
                var result = erased switch
                {
                    ProjectPrefixErased done => new SweepOutcome(done.DeletedAny, null),
                    PlatformUnavailable unavailable => new SweepOutcome(false, unavailable),
                };
                if (result.Failure is { } failure)
                {
                    logger.ZLogWarning($"Closed project {after} file sweep deferred: {failure.Message}");
                    continue;
                }

                var closedAt = row.Number(2);
                if (closedAt > cutoff)
                {
                    var firstSeenMs = hasState ? state.FirstSeenMs : sweptAt;
                    var fastUntilMs = Math.Min(closedAt, firstSeenMs) + fastGraceMs;
                    if (result.DeletedAny || sweptAt < fastUntilMs)
                    {
                        graceSweeps[after] = new(firstSeenMs, sweptAt, 0);
                    }
                    else
                    {
                        var step = hasState ? state.BackoffStep : 0;
                        var delayMs = (long)GraceBackoff[step].TotalMilliseconds;
                        var nextSweepMs = Math.Min(sweptAt + delayMs, closedAt + graceMs);
                        graceSweeps[after] = new(firstSeenMs, nextSweepMs, Math.Min(step + 1, GraceBackoff.Length - 1));
                    }

                    continue;
                }

                var marked = await store.ExecuteAsync(
                    [SqlValues.Statement(
                        "UPDATE platform_closed_file_projects SET swept_at = ? WHERE project_id = ? AND (swept_at IS NULL OR swept_at < closed_at + ?)",
                        SqlValues.Number(sweptAt), SqlValues.Text(after), SqlValues.Number(graceMs))],
                    SqlContext.Fast,
                    cancellationToken);
                var markFailure = marked switch
                {
                    SqlExecuteResponse => null,
                    PlatformConflict conflict => conflict.Message,
                    PlatformUnavailable unavailable => unavailable.Message,
                };
                if (markFailure is not null)
                {
                    logger.ZLogWarning($"Closed project {after} file sweep marker deferred: {markFailure}");
                }
                else
                {
                    graceSweeps.Remove(after);
                }
            }

            if (projects.Count < PageSize)
            {
                PruneGraceSweeps(seen);
                return;
            }
        }
    }

    private void PruneGraceSweeps(HashSet<string> seen)
    {
        if (graceSweeps.Count == 0)
        {
            return;
        }

        foreach (var projectId in graceSweeps.Keys.Where(id => !seen.Contains(id)).ToArray())
        {
            graceSweeps.Remove(projectId);
        }
    }

    private IReadOnlyList<SqlRow>? DeferredRows(string message)
    {
        logger.ZLogWarning($"Closed project file sweep deferred: {message}");
        return null;
    }

    private IReadOnlyList<T> Deferred<T>(string message)
    {
        logger.ZLogWarning($"Closed project file sweep deferred: {message}");
        return [];
    }

    private readonly record struct GraceSweepState(long FirstSeenMs, long NextSweepMs, int BackoffStep);

    private readonly record struct SweepOutcome(bool DeletedAny, PlatformUnavailable? Failure);
}

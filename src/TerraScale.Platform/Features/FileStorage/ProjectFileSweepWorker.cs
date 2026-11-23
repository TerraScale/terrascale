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

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (options.Files is null)
        {
            return;
        }

        using var timer = new PeriodicTimer(Interval, timeProvider);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            if (!readiness.SchemaReady)
            {
                continue;
            }

            try
            {
                await SweepAsync(stoppingToken);
            }
            catch (Exception error) when (!stoppingToken.IsCancellationRequested && error is not OutOfMemoryException)
            {
                logger.ZLogWarning(error, $"Closed project file sweep failed");
            }
        }
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

        var after = string.Empty;
        while (true)
        {
            var page = await store.ReadAsync(SqlValues.Statement(
                "SELECT project_id, organization_id FROM platform_closed_file_projects WHERE project_id > ? ORDER BY project_id LIMIT ?",
                SqlValues.Text(after), SqlValues.Number(PageSize)), cancellationToken);
            IReadOnlyList<SqlRow> projects = page switch
            {
                SqlExecuteResponse { Results: var results } => results[0].RowsOf().ToArray(),
                PlatformConflict conflict => Deferred<SqlRow>(conflict.Message),
                PlatformUnavailable unavailable => Deferred<SqlRow>(unavailable.Message),
            };
            foreach (var row in projects)
            {
                after = row.Text(0);
                var erased = await erasure.SweepAsync(Ulid.Parse(after), Ulid.Parse(row.Text(1)), regions, cancellationToken);
                var failure = erased switch { ProjectPrefixErased => null, PlatformUnavailable unavailable => unavailable };
                if (failure is not null)
                {
                    logger.ZLogWarning($"Closed project {after} file sweep deferred: {failure.Message}");
                }
            }

            if (projects.Count < PageSize)
            {
                return;
            }
        }
    }

    private IReadOnlyList<T> Deferred<T>(string message)
    {
        logger.ZLogWarning($"Closed project file sweep deferred: {message}");
        return [];
    }
}

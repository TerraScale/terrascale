using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace TerraBase.Continuity;

public sealed class CommitLogArchiverService(
    BackupContinuityService continuity,
    IContinuityDatabaseCatalog catalog,
    ContinuityOptions options,
    ContinuityMetrics metrics,
    TimeProvider timeProvider,
    ILogger<CommitLogArchiverService> logger) : BackgroundService
{
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

        await Parallel.ForEachAsync(
            databases,
            new ParallelOptions { MaxDegreeOfParallelism = options.ArchiveParallelism, CancellationToken = stoppingToken },
            ArchiveAsync);
    }

    private async ValueTask ArchiveAsync(Ulid databaseId, CancellationToken stoppingToken)
    {
        try
        {
            var archived = await continuity.ArchiveAsync(databaseId, forceSnapshot: false, stoppingToken);
            var failure = archived switch
            {
                BackupStatus => (string?)null,
                ContinuityNotFound => null,
                ContinuityUnavailable unavailable => unavailable.Message,
            };
            if (failure is not null)
            {
                metrics.ArchiveFailed();
                ContinuityLog.ArchiveFailed(logger, databaseId, failure);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            metrics.ArchiveFailed();
            ContinuityLog.ArchiveFaulted(logger, exception, databaseId);
        }
    }
}

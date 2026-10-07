using TerraBase.Configuration;
using TerraBase.Features.Management.Queries;

namespace TerraBase.Features.Management.Usage;

/// <summary>
/// Flushes <see cref="UsageMeter"/> and <see cref="QueryFieldRecorder"/> every 10 seconds and once more on shutdown. A catalog
/// follower writes no catalog: its counts stay in memory, bounded by the databases it serves, and are lost on restart.
/// </summary>
internal sealed partial class UsageFlushService(
    UsageMeter meter, QueryFieldRecorder recorder, TimeProvider time, TerraBaseOptions options, ILogger<UsageFlushService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(10);

    // A clean shutdown saves what is pending, within the host's shutdown timeout. What this flush cannot save is lost
    // and logged: usage counts are best-effort dashboard figures, so they never fail or extend a shutdown.
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
        foreach (var flush in options.CatalogPrimary is null ? Flushes : [])
        {
            try
            {
                await flush(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                LogShutdownFlushFailed(logger, error);
            }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (options.CatalogPrimary is not null)
        {
            return;
        }

        using var timer = new PeriodicTimer(Interval, time);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            await FlushAsync(stoppingToken).ConfigureAwait(false);
        }
    }

    // Each flush runs on its own; a failed one keeps its counts pending for the next and never stops the process.
    private async Task FlushAsync(CancellationToken cancellationToken)
    {
        foreach (var flush in Flushes)
        {
            try
            {
                await flush(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error) when (!cancellationToken.IsCancellationRequested)
            {
                LogFlushFailed(logger, error);
            }
        }
    }

    private Func<CancellationToken, ValueTask>[] Flushes => [meter.FlushAsync, recorder.FlushAsync];

    [LoggerMessage(Level = LogLevel.Warning, Message = "Flushing usage counts failed; they stay pending")]
    private static partial void LogFlushFailed(ILogger logger, Exception error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Flushing usage counts at shutdown failed; the unsaved counts are lost")]
    private static partial void LogShutdownFlushFailed(ILogger logger, Exception error);
}

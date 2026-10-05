namespace TerraScale.Auth.Features.Identity;

/// <summary>
/// Deletes dead sessions every ten minutes on every replica. Deletes are version-conditional, so concurrent passes are
/// safe; a failed pass is retried at the next tick, and a dead session grants nothing while it waits.
/// </summary>
public sealed partial class SessionPruner(Sessions sessions, ILogger<SessionPruner> logger) : BackgroundService
{
    private const int BatchSize = 500;

    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(10);

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                await sessions.PruneAsync(BatchSize, stoppingToken).ConfigureAwait(false);
            }
            catch (Exception error) when (!stoppingToken.IsCancellationRequested)
            {
                LogPruneFailed(logger, error);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Session pruning failed; retrying at the next interval")]
    private static partial void LogPruneFailed(ILogger logger, Exception exception);
}

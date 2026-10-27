using ZLogger;
using TerraScale.Authentication.Features.Platform;

namespace TerraScale.Authentication.Features.Identity;

/// <summary>
/// Deletes dead sessions and spent code records every ten minutes on every replica. Deletes are version-conditional,
/// so concurrent passes are safe; a failed pass is retried at the next tick, and dead records grant nothing while they wait.
/// </summary>
public sealed class SessionPruner(Sessions sessions, Passcodes passcodes, StepUpProofs proofs, ILogger<SessionPruner> logger) : BackgroundService
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
                await passcodes.PruneAsync(BatchSize, stoppingToken).ConfigureAwait(false);
                await proofs.PruneAsync(BatchSize, stoppingToken).ConfigureAwait(false);
            }
            catch (Exception error) when (!stoppingToken.IsCancellationRequested)
            {
                logger.ZLogWarning(error, $"Session or code pruning failed; retrying at the next interval");
            }
        }
    }
}

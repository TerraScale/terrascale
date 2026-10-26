using ZLogger;

namespace TerraScale.Authentication.Features.Oidc;

/// <summary>Always-on cleanup on every serving replica; each tick examines one page in each OIDC collection.</summary>
public sealed class OidcPruner(OidcRetention retention, TimeProvider clock, ILogger<OidcPruner> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(10);

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, clock);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                var tokens = await retention.PruneTokensAsync(clock.GetUtcNow(), null, stoppingToken).ConfigureAwait(false);
                var authorizations = await retention.PruneAuthorizationsAsync(clock.GetUtcNow(), null, stoppingToken).ConfigureAwait(false);
                logger.ZLogInformation($"OIDC retention pruned {tokens} token records and {authorizations} authorization records");
            }
            catch (Exception error) when (!stoppingToken.IsCancellationRequested)
            {
                logger.ZLogWarning(error, $"OIDC retention failed; retrying at the next interval");
            }
        }
    }
}

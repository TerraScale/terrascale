using Microsoft.EntityFrameworkCore;

namespace TerraScale.Dashboard.Platform.Identity;

public sealed class SessionRefresher(DashboardSessions sessions, TimeProvider clock,
    ILogger<SessionRefresher> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(15);

    private const int BatchSize = 200;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, clock);
        do
        {
            try
            {
                var expiring = await sessions.ReadTokensExpiringAsync(BatchSize, stoppingToken);
                foreach (var row in expiring)
                {
                    try { await sessions.RefreshBeforeExpiryAsync(row, stoppingToken); }
                    catch (Exception error) when (error is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
                    {
                        logger.ADashboardSessionCouldNotBeRefreshed(row.Id, error.GetType().Name);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception error) when (error is DbUpdateException or TimeoutException or HttpRequestException or IOException or InvalidOperationException)
            {
                logger.SessionRefreshIsUnavailable(error.GetType().Name);
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}

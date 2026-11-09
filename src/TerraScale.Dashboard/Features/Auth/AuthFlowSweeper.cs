using Microsoft.EntityFrameworkCore;
using TerraScale.Dashboard.Platform.Persistence;

namespace TerraScale.Dashboard.Features.Auth;

public sealed class AuthFlowSweeper(IDbContextFactory<DashboardDbContext> storage, DashboardStateInitializer initializer, TimeProvider time,
    ILogger<AuthFlowSweeper> logger) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await initializer.WhenInitialized.WaitAsync(stoppingToken);
            using var timer = new PeriodicTimer(Interval, time);
            do
            {
                try
                {
                    var now = time.GetUtcNow().UtcDateTime;
                    await using var db = await storage.CreateDbContextAsync(stoppingToken);
                    await db.AuthFlows.Where(flow => flow.ExpiresAt <= now).ExecuteDeleteAsync(stoppingToken);
                    await db.Sessions.Where(session => session.ExpiresAt <= now).ExecuteDeleteAsync(stoppingToken);
                }
                catch (Exception error) when (!stoppingToken.IsCancellationRequested)
                {
                    logger.ExpiredAuthFlowsWereNotSwept(error.GetType().Name);
                }
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}

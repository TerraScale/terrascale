using Microsoft.EntityFrameworkCore;
using TerraScale.Dashboard.Platform.Mcp;

namespace TerraScale.Dashboard.Platform.Persistence;

public sealed class StorageSweeper(IDbContextFactory<DashboardDbContext> storage, DashboardStateInitializer initializer, DashboardSessions sessions, TimeProvider time,
    ILogger<StorageSweeper> logger, BackgroundJobs jobs) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(10);

    private const int BatchSize = 500;

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
                    await jobs.RunAsync("storage-sweep", Interval, async (claim, token) =>
                    {
                        var now = time.GetUtcNow().UtcDateTime;
                        await using var db = await storage.CreateDbContextAsync(token);
                        await db.AuthFlows.Where(flow => flow.ExpiresAt <= now).ExecuteDeleteAsync(token);
                        await db.Sessions.Where(session => session.Kind == SessionRow.Credential && session.ExpiresAt <= now).ExecuteDeleteAsync(token);
                        await db.McpCodes.Where(code => code.ExpiresAt <= now).ExecuteDeleteAsync(token);
                        await McpRegistrations.ReclaimUnfinishedAsync(db, now, token);
                        await sessions.ExpireBrowserSessionsAsync(BatchSize, token);
                    }, stoppingToken);
                }
                catch (Exception error) when (!stoppingToken.IsCancellationRequested)
                {
                    logger.ExpiredRowsWereNotSwept(error.GetType().Name);
                }
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}

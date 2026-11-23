using Dapper;
using TerraScale.Dashboard.Features.Billing;
using TerraScale.Dashboard.Platform.Mcp;

namespace TerraScale.Dashboard.Platform.Persistence;

public sealed class StorageSweeper(DashboardDb storage, DashboardDbUpRunner initializer, DashboardSessions sessions, TimeProvider time,
    ILogger<StorageSweeper> logger, BackgroundJobs jobs, BillingSchemaGate gate) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(10);

    private const int BatchSize = 500;

    private const string Sweep = """
        DELETE FROM auth_flows WHERE expires_at <= @Now;
        DELETE FROM sessions WHERE kind = @Credential AND expires_at <= @Now;
        DELETE FROM mcp_codes WHERE expires_at <= @Now
        """;

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
                    if (!await gate.AllowsAsync(stoppingToken))
                    {
                        continue;
                    }

                    await jobs.RunAsync("storage-sweep", Interval, async (claim, token) =>
                    {
                        var now = time.GetUtcNow().UtcDateTime;
                        await using (var connection = await storage.OpenAsync(token))
                        {
                            await connection.ExecuteAsync(Sweep, new { Now = now, SessionRow.Credential });
                        }

                        await McpRegistrations.ReclaimUnfinishedAsync(storage, now, token);
                        await sessions.ExpireBrowserSessionsAsync(BatchSize, token);
                    }, stoppingToken);
                }
                catch (Exception error) when (!stoppingToken.IsCancellationRequested)
                {
                    logger.ExpiredRowsWereNotSwept(error.GetType().Name);
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}

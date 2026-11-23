using ZLogger;

namespace TerraBase.Features.Management;

internal sealed class DatabaseExpiry(ManagementService management, TimeProvider time, ILogger<DatabaseExpiry> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1), time);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            var deleted = await management.SweepExpiredAsync(stoppingToken);
            if (deleted > 0)
            {
                logger.ZLogInformation($"Deleted {deleted:@Deleted} expired databases");
            }
        }
    }
}

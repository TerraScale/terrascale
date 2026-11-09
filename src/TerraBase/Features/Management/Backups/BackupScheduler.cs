using ZLogger;

namespace TerraBase.Features.Management.Backups;

internal sealed class BackupScheduler(IManagedBackupService backups, TimeProvider time, ILogger<BackupScheduler> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await backups.RecoverInterruptedRestoresAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (Exception error) when (!stoppingToken.IsCancellationRequested)
        {
            logger.ZLogWarning(error, $"Recovering interrupted restore operations failed; the next pass tries again");
        }

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1), time);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                await backups.RunScheduleAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception error) when (!stoppingToken.IsCancellationRequested)
            {
                logger.ZLogWarning(error, $"Backup scheduler pass failed");
            }
        }
    }
}

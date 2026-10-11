using TerraBase.Configuration;
using ZLogger;

namespace TerraBase.Features.Management.Backups;

/// <summary>Once a minute, takes the scheduled backups that are due and applies retention; on the catalog primary only.</summary>
internal sealed class BackupScheduler(IManagedBackupService backups, TimeProvider time, TerraBaseOptions options, ILogger<BackupScheduler> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (options.CatalogPrimary is not null)
        {
            return;
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
                // A failed pass must not stop the host; the next tick retries.
                logger.ZLogWarning(error, $"Backup scheduler pass failed");
            }
        }
    }
}

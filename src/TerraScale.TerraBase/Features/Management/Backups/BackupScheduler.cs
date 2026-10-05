namespace TerraScale.TerraBase.Features.Management.Backups;

/// <summary>Once a minute, takes the scheduled backups that are due and applies retention.</summary>
internal sealed partial class BackupScheduler(IManagedBackupService backups, TimeProvider time, ILogger<BackupScheduler> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
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
                LogPassFailed(logger, error);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Backup scheduler pass failed")]
    private static partial void LogPassFailed(ILogger logger, Exception error);
}

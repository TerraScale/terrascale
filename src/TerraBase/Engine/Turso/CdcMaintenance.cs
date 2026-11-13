using ZLogger;

namespace TerraBase.Engine.Turso;

internal sealed class CdcMaintenance(TursoStorageEngine engine, TimeProvider time, ILogger<CdcMaintenance> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30), time);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            await foreach (var database in engine.ListDatabasesAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    if (await engine.OpenDatabaseAsync(database, stoppingToken).ConfigureAwait(false) is TursoDatabaseStore store)
                    {
                        await store.TrimIdleAsync(stoppingToken).ConfigureAwait(false);
                    }
                }
                catch (Exception error) when (error is not OperationCanceledException and not OutOfMemoryException)
                {
                    logger.ZLogWarning(error, $"Idle change history maintenance failed");
                }
            }
        }
    }
}

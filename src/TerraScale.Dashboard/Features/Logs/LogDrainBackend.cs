using System.Text.Json;
using TerraScale.Dashboard.Platform.Clients;

namespace TerraScale.Dashboard.Features.Logs
{
    public sealed class LogDrainSource(TerraBaseHttpClient terraBase) : ILogDrainSource
    {
        public async Task<DatabaseLogBatch> ReadAsync(string projectId, string databaseId, string after, CancellationToken cancellationToken)
        {
            var result = await terraBase.ReadLogsAsync(ServiceCaller.Operator, projectId, databaseId, after, cancellationToken) switch
            {
                TerraBaseRequestLogPage page => page,
                TerraBaseRefusal { Status: StatusCodes.Status404NotFound } => new TerraBaseRequestLogPage(databaseId, [], false, false, after),
                TerraBaseRefusal refusal => throw new IOException("Database request logs are unavailable.", refusal.Exception()),
            };

            var batch = DatabaseLogBackend.Batch(result);
            DatabaseLogBackend.Validate(batch, databaseId, "", cursor: true);
            return batch;
        }
    }

    public sealed class LogDrainWorker(LogDrains drains, TimeProvider clock, ILogger<LogDrainWorker> logger) : BackgroundService
    {
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15), clock);
            do
            {
                try { await drains.RunOnceAsync(stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
                catch (Exception error) when (error is TerraBaseException or TimeoutException or HttpRequestException or IOException or JsonException or InvalidOperationException or OperationCanceledException)
                {
                    logger.LogDrainsAreUnavailableRetryingShortly(error.GetType().Name);
                }
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }
    }
}

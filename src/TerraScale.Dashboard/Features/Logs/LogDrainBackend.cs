using System.Text.Json;
using TerraScale.Dashboard.Platform.Rpc;
using TerraScale.TerraBase.Contracts.Logs;
using TerraScale.TerraBase.Contracts;

namespace TerraScale.Dashboard.Features.Logs
{
    // The same document as the public database logs route, read over the dashboard's private TerraBase listener.
    public sealed class LogDrainSource(TerraBaseRpcClient rpc) : ILogDrainSource
    {
        public async Task<DatabaseLogBatch> ReadAsync(string projectId, string databaseId, string after, CancellationToken cancellationToken)
        {
            TerraBaseRequestLogPage result;
            try
            {
                result = await rpc.Platform<ILogsService>().WithCancellationToken(cancellationToken).ReadDrainAsync(projectId, databaseId, after);
            }
            catch (TerraBaseNotFoundException)
            {
                return new(databaseId, [], false, false, after);
            }
            catch (TerraBaseException error)
            {
                throw new IOException("Database request logs are unavailable.", error);
            }

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
                catch (Exception error) when (error is HttpRequestException or IOException or JsonException or InvalidOperationException or OperationCanceledException)
                {
                    logger.LogDrainsAreUnavailableRetryingShortly(error.GetType().Name);
                }
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }
    }
}

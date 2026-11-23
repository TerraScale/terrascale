using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace TerraBase.Continuity;

public sealed class ContinuityOperationWorker(
    BackupContinuityService continuity,
    DatabaseRestorer restorer,
    ILogger<ContinuityOperationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var cleaned = restorer.RecoverStaging() switch
        {
            Done => null,
            Unavailable unavailable => unavailable.Message,
        };
        if (cleaned is not null)
        {
            ContinuityLog.StagingCleanupFailed(logger, cleaned);
        }

        await foreach (var work in continuity.PendingOperationsAsync(stoppingToken).ConfigureAwait(false))
        {
            Report(await RunAsync(work, stoppingToken).ConfigureAwait(false));
        }
    }

    private async ValueTask<FinishedOperation> RunAsync(OperationWork work, CancellationToken stoppingToken)
    {
        try
        {
            return await continuity.ExecuteAsync(work, stoppingToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            ContinuityLog.OperationFaulted(logger, exception, work.OperationId);
            return await continuity.FaultAsync(work, exception.Message).ConfigureAwait(false);
        }
    }

    private void Report(FinishedOperation finished)
    {
        var state = ManifestCodec.StateLabel(finished.Operation.State);
        ContinuityLog.OperationFinished(logger, finished.Operation.OperationId, finished.Kind, state, finished.Operation.TargetDatabaseId);
        if (finished.Operation.FailureMessage is { } failure)
        {
            ContinuityLog.OperationFailed(logger, finished.Operation.OperationId, failure);
        }

        if (finished.RecordFailure is { } recordFailure)
        {
            ContinuityLog.OperationRecordFailed(logger, finished.Operation.OperationId, recordFailure);
        }
    }
}

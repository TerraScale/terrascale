using System.Diagnostics.Metrics;
using TerraBase.Engine;
using TerraBase.Hosting;
using ZLogger;

namespace TerraBase.Features.Replication;

/// <summary>Node-local durable work and a bounded notification that work may exist.</summary>
internal interface IRebuildRecovery
{
    /// <summary>Enumerates persisted records in bounded pages, without an in-memory waiting list.</summary>
    IAsyncEnumerable<RebuildRecord> PendingAsync(CancellationToken cancellationToken);

    /// <summary>Consumes one coalesced notification, emitted only after a fault record is durable.</summary>
    Task WaitAsync(CancellationToken cancellationToken);
}

/// <summary>Resumes persisted rebuilds at startup, after a durable fault, and when a refused peer becomes available.</summary>
internal sealed class RebuildRecovery(
    IStorageEngine engine, IRebuildRecovery recovery, IReplicaRebuild rebuild, IHostApplicationLifetime lifetime,
    TimeProvider time, IMeterFactory meters, ILogger<RebuildRecovery> logger) : BackgroundService
{
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(60);
    private readonly Counter<long> _outcomes = meters.Create(Telemetry.MeterName).CreateCounter<long>(
        "terrabase.replication.rebuilds", "{rebuild}", "Replica rebuild attempts by their outcome.");

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        // Recover SYSTEM intents before the remaining hosted services start.
        _ = await engine.OpenSystemDatabaseAsync(cancellationToken).ConfigureAwait(false);
        await base.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // SYSTEM recovery is complete; wait until catalog startup releases its storage admission.
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = lifetime.ApplicationStarted.Register(() => started.TrySetResult());
            await started.Task.WaitAsync(stoppingToken).ConfigureAwait(false);
            using var timer = new PeriodicTimer(RetryInterval, time);
            var tick = timer.WaitForNextTickAsync(stoppingToken).AsTask();
            while (!stoppingToken.IsCancellationRequested)
            {
                using var waiting = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                var wake = recovery.WaitAsync(waiting.Token);
                _ = await Task.WhenAny(wake, tick).ConfigureAwait(false);
                if (wake.IsCompleted)
                {
                    await wake.ConfigureAwait(false);
                }
                else
                {
                    await waiting.CancelAsync().ConfigureAwait(false);
                    await wake.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                }

                if (tick.IsCompleted)
                {
                    if (!await tick.ConfigureAwait(false))
                    {
                        return;
                    }

                    tick = timer.WaitForNextTickAsync(stoppingToken).AsTask();
                }

                try
                {
                    await RunPendingAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (Exception error) when (!stoppingToken.IsCancellationRequested)
                {
                    logger.ZLogWarning(error, $"Replica rebuild recovery could not read its durable work; a later pass retries");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // A stopped recovery worker exits normally, including when joined before host shutdown.
        }
    }

    internal async Task RunPendingAsync(CancellationToken cancellationToken)
    {
        await foreach (var record in recovery.PendingAsync(cancellationToken).ConfigureAwait(false))
        {
            try
            {
                var outcome = await rebuild.RebuildAsync(record.Database, record.OldIncarnation, cancellationToken).ConfigureAwait(false);
                _outcomes.Add(1, new KeyValuePair<string, object?>("outcome", outcome switch
                {
                    RebuildOutcome.Completed => "completed",
                    RebuildOutcome.Busy => "busy",
                    _ => "refused",
                }));
                if (outcome is RebuildOutcome.Busy)
                {
                    return;
                }

                if (outcome is RebuildOutcome.Refused refused)
                {
                    logger.ZLogWarning($"Replica rebuild remains pending: {refused.Reason:@Reason}");
                }
            }
            catch (Exception error) when (!cancellationToken.IsCancellationRequested)
            {
                logger.ZLogWarning(error, $"Replica rebuild failed; its durable record remains pending");
            }
        }
    }
}

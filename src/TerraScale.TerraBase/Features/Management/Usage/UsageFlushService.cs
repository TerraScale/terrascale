using Google.Protobuf;
using TerraBase.Configuration;
using TerraBase.Features.Management.Queries;
using TerraBase.Features.Replication;
using TerraBase.Features.Replication.V1;

namespace TerraBase.Features.Management.Usage;

/// <summary>
/// Flushes <see cref="UsageMeter"/>, <see cref="QueryFieldRecorder"/> and <see cref="QueryShapeRecorder"/> every 10 seconds and once more on shutdown. A catalog
/// follower writes no catalog: it sends its usage counts to its primary as often instead, and keeps them pending while the
/// primary does not acknowledge them. Its query field and shape counts stay in bounded memory and are lost
/// on restart.
/// </summary>
internal sealed partial class UsageFlushService(
    UsageMeter meter, QueryFieldRecorder recorder, QueryShapeRecorder shapes, TimeProvider time, TerraBaseOptions options, ILogger<UsageFlushService> logger,
    ICatalogPrimaryClient? primary = null) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(10);

    // This process's batches, numbered by UsageMeter.SendAsync.
    private readonly ByteString _epoch = ByteString.CopyFrom(Guid.NewGuid().ToByteArray());

    // A clean shutdown saves what is pending, within the host's shutdown timeout. What this flush cannot save is lost
    // and logged: usage counts are best-effort dashboard figures, so they never fail or extend a shutdown.
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
        foreach (var flush in options.CatalogPrimary is null ? Flushes : [DrainAsync])
        {
            try
            {
                await flush(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                LogShutdownFlushFailed(logger, error);
            }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, time);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            // The tick resumes this loop on the clock's callback thread. Leave it, as the pressure samplers do (TS-517): a
            // flush reads the clock and the catalog, and a test clock advanced by days would otherwise flush inline.
            await Task.Yield();
            await FlushAsync(stoppingToken).ConfigureAwait(false);
        }
    }

    // Each flush runs on its own; a failed one keeps its counts pending for the next and never stops the process.
    internal async Task FlushAsync(CancellationToken cancellationToken)
    {
        foreach (var flush in Flushes)
        {
            try
            {
                await flush(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error) when (!cancellationToken.IsCancellationRequested)
            {
                LogFlushFailed(logger, error);
            }
        }
    }

    private Func<CancellationToken, ValueTask>[] Flushes => options.CatalogPrimary is null ? [meter.FlushAsync, recorder.FlushAsync, shapes.FlushAsync] : [SendOnceAsync];

    // A tick sends one bounded batch; a follower's backlog drains a batch per tick.
    private async ValueTask SendOnceAsync(CancellationToken cancellationToken) => await meter.SendAsync(SendAsync, cancellationToken).ConfigureAwait(false);

    // At shutdown a follower sends batch after batch, while the primary acknowledges them and the shutdown timeout lasts.
    private async ValueTask DrainAsync(CancellationToken cancellationToken)
    {
        while (await meter.SendAsync(SendAsync, cancellationToken).ConfigureAwait(false))
        {
        }
    }

    // Counts past MaxCount, which no real hour reaches, are not sent: the primary would refuse the whole batch for good.
    private Task<bool> SendAsync(long sequence, KeyValuePair<UsageKey, OpCounts>[] batch, CancellationToken cancellationToken)
    {
        var request = new UsageBatch { Protocol = UsageReceiver.Protocol, Epoch = _epoch, Sequence = (ulong)sequence, Sender = options.NodeName };
        request.Counts.AddRange(batch.Select(entry => new UsageCount
        {
            OrganizationId = entry.Key.Database.OrganizationId,
            ProjectId = entry.Key.Database.ProjectId,
            DatabaseId = entry.Key.Database.DatabaseId,
            Hour = entry.Key.Hour,
            Reads = (ulong)Math.Min(entry.Value.Reads, UsageMeter.MaxCount),
            Writes = (ulong)Math.Min(entry.Value.Writes, UsageMeter.MaxCount),
            Scope = (Replication.V1.UsageScope)entry.Key.Scope,
        }));
        return primary!.AddUsageAsync(request, cancellationToken);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Flushing usage counts failed; they stay pending")]
    private static partial void LogFlushFailed(ILogger logger, Exception error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Flushing usage counts at shutdown failed; the unsaved counts are lost")]
    private static partial void LogShutdownFlushFailed(ILogger logger, Exception error);
}

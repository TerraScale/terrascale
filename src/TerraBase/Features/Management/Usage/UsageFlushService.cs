using TerraBase.Configuration;
using TerraBase.Engine;
using TerraBase.Features.Management.Queries;
using TerraBase.Features.Replication;
using TerraBase.Tenancy;
using ZLogger;

namespace TerraBase.Features.Management.Usage;

/// <summary>
/// Flushes <see cref="UsageMeter"/>, <see cref="QueryFieldRecorder"/> and <see cref="QueryShapeRecorder"/> every 10 seconds and once more on shutdown,
/// into this machine's catalog.
/// </summary>
internal sealed class UsageFlushService(
    UsageMeter meter, QueryFieldRecorder recorder, QueryShapeRecorder shapes, TimeProvider time, TerraBaseOptions options, ILogger<UsageFlushService> logger,
    IStorageEngine engine, IReplicationStatus replication) : BackgroundService
{
    // Storage is sampled once per minute bucket, so an idle node neither lists databases nor writes usage every tick.
    private const int MaxSamplesPerMinute = 128;
    private long _sampledMinute = -1;
    // The owner of the last database sampled: each minute samples the next MaxSamplesPerMinute owners in ordinal order after it,
    // wrapping, so every database is sampled in turn whatever order the engine lists them in.
    private string? _cursor;
    // Databases already sampled in _samplingMinute, so a pass retried after a failure records none of them twice. At most the cap.
    private readonly HashSet<string> _sampled = [with(StringComparer.Ordinal)];
    private long _samplingMinute = -1;
    private readonly SemaphoreSlim _sampling = new(1, 1);

    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(10);

    // A clean shutdown saves what is pending, within the host's shutdown timeout. What this flush cannot save is lost
    // and logged: usage counts are best-effort dashboard figures, so they never fail or extend a shutdown.
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
        foreach (var flush in Flushes)
        {
            await ShutdownFlushAsync(flush, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ShutdownFlushAsync(Func<CancellationToken, ValueTask> flush, CancellationToken cancellationToken)
    {
        try
        {
            await flush(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            logger.ZLogWarning(error, $"Flushing workload observations at shutdown failed; the unsaved counts are lost");
        }
    }

    // Storage sampling lists every database, so it runs on its own loop: a slow volume never delays usage delivery.
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.WhenAll(LoopAsync(FlushAsync, stoppingToken), LoopAsync(SampleStorageAsync, stoppingToken));

    private async Task LoopAsync(Func<CancellationToken, Task> work, CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, time);
        while (await NextTickAsync(timer, stoppingToken).ConfigureAwait(false))
        {
            // The tick resumes this loop on the clock's callback thread. Leave it, as the pressure samplers do (TS-517): a
            // flush reads the clock and the catalog, and a test clock advanced by days would otherwise flush inline.
            await Task.Yield();
            try
            {
                await work(stoppingToken).ConfigureAwait(false);
            }
            // FlushAsync stops catching once shutdown begins, so any failure would otherwise fault this task and the host's
            // shutdown. StopAsync's flush saves what is still pending and logs what it cannot save.
            catch (Exception) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    // A loop idle at shutdown ends cleanly, like one stopped mid-work.
    private static async Task<bool> NextTickAsync(PeriodicTimer timer, CancellationToken stoppingToken)
    {
        try
        {
            return await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return false;
        }
    }

    // One storage sampling pass at a time; a failed one is logged and retried on the next tick.
    internal async Task SampleStorageAsync(CancellationToken cancellationToken)
    {
        await _sampling.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await SampleAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (!cancellationToken.IsCancellationRequested)
        {
            logger.ZLogWarning(error, $"Flushing workload observations failed; they stay pending");
        }
        finally
        {
            _sampling.Release();
        }
    }

    // Each flush runs on its own; a failed one keeps its counts pending for the next and never stops the process.
    internal async Task FlushAsync(CancellationToken cancellationToken)
    {
        foreach (var flush in new Func<CancellationToken, ValueTask>[] { ReportActive, RecordPeers }.Concat(Flushes))
        {
            try
            {
                await flush(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error) when (!cancellationToken.IsCancellationRequested)
            {
                logger.ZLogWarning(error, $"Flushing workload observations failed; they stay pending");
            }
        }
    }

    private Func<CancellationToken, ValueTask>[] Flushes => [meter.FlushAsync, recorder.FlushAsync, shapes.FlushAsync];

    private ValueTask ReportActive(CancellationToken cancellationToken)
    {
        meter.ReportActive();
        return ValueTask.CompletedTask;
    }

    private ValueTask RecordPeers(CancellationToken cancellationToken)
    {
        if (!UsageMeter.ValidRegion(options.Region))
        {
            return ValueTask.CompletedTask;
        }

        var status = replication.Snapshot();
        if (status.Initialized)
        {
            foreach (var group in status.Peers.SelectMany(peer => peer.Databases.Select(database => (peer.Region, Database: database)))
                .GroupBy(observation => (observation.Database.OrganizationId, observation.Database.ProjectId, observation.Database.DatabaseId, observation.Region)))
            {
                var (organization, project, database, region) = group.Key;
                meter.RecordPeerState(new(organization, PlanTier.Hobby, project, database), region,
                    group.Max(observation => observation.Database.DurableLag), group.Max(observation => observation.Database.CatchupSeconds) * 1000,
                    group.Any(observation => observation.Database.Diverged), group.Max(observation => observation.Database.ConvergenceSeconds) * 1000);
            }
        }

        return ValueTask.CompletedTask;
    }

    private async ValueTask SampleAsync(CancellationToken cancellationToken)
    {
        if (!UsageMeter.ValidRegion(options.Region))
        {
            return;
        }

        // Every sample of a pass belongs to the minute it started in, however long sizing takes.
        var sampledAt = time.GetUtcNow();
        var minute = sampledAt.ToUnixTimeSeconds() / 60;
        if (minute == _sampledMinute)
        {
            return;
        }

        if (minute != _samplingMinute)
        {
            _samplingMinute = minute;
            _sampled.Clear();
        }

        // The smallest owners after the cursor, then from the start: two bounded selections over one listing.
        var after = new SortedDictionary<string, TenantContext>(StringComparer.Ordinal);
        var wrapped = new SortedDictionary<string, TenantContext>(StringComparer.Ordinal);
        await foreach (var database in engine.ListDatabasesAsync(cancellationToken).ConfigureAwait(false))
        {
            var key = $"{database.OrganizationId}/{database.ProjectId}/{database.DatabaseId}";
            var selection = _cursor is null || string.CompareOrdinal(key, _cursor) > 0 ? after : wrapped;
            selection[key] = database;
            if (selection.Count > MaxSamplesPerMinute)
            {
                _ = selection.Remove(selection.Keys.Last());
            }
        }

        string? last = null;
        foreach (var (key, database) in after.Concat(wrapped).Take(MaxSamplesPerMinute))
        {
            last = key;
            try
            {
                if (!_sampled.Contains(key) && await engine.GetDatabaseSizeAsync(database, cancellationToken).ConfigureAwait(false) is { } size)
                {
                    meter.RecordStorage(database, size, sampledAt);
                    _ = _sampled.Add(key);
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // One database's failure, such as its file vanishing while sized, costs only its own sample.
            }
        }

        // Only a complete pass marks its minute sampled and moves the cursor; a listing failure leaves both to the next tick.
        _sampledMinute = minute;
        _cursor = last ?? _cursor;
    }
}

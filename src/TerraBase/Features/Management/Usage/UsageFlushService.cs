using TerraBase.Configuration;
using TerraBase.Engine;
using TerraBase.Features.Management.Queries;
using TerraBase.Features.Management.Logs;
using TerraBase.Tenancy;
using ZLogger;

namespace TerraBase.Features.Management.Usage;

internal sealed class UsageFlushService(
    UsageMeter meter, QueryFieldRecorder recorder, QueryShapeRecorder shapes, TimeProvider time, TerraBaseOptions options, ILogger<UsageFlushService> logger,
    IStorageEngine engine, IRequestLogs logs) : BackgroundService
{
    private const int MaxSamplesPerMinute = 128;
    private long _sampledMinute = -1;
    private string? _cursor;
    private readonly HashSet<string> _sampled = [with(StringComparer.Ordinal)];
    private long _samplingMinute = -1;
    private readonly SemaphoreSlim _sampling = new(1, 1);

    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(10);

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

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.WhenAll(LoopAsync(FlushAsync, stoppingToken), LoopAsync(SampleStorageAsync, stoppingToken), LoopAsync(RefreshMonthsAsync, stoppingToken));

    private async Task LoopAsync(Func<CancellationToken, Task> work, CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, time);
        while (await NextTickAsync(timer, stoppingToken).ConfigureAwait(false))
        {
            await Task.Yield();
            try
            {
                await work(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

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

    internal async Task FlushAsync(CancellationToken cancellationToken)
    {
        foreach (var flush in new Func<CancellationToken, ValueTask>[] { ReportActive }.Concat(Flushes))
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

    private async Task RefreshMonthsAsync(CancellationToken cancellationToken)
    {
        try
        {
            await meter.RefreshMonthsAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (!cancellationToken.IsCancellationRequested)
        {
            logger.ZLogWarning(error, $"Reading saved monthly usage failed; the next tick retries");
        }
    }

    private Func<CancellationToken, ValueTask>[] Flushes => [meter.FlushAsync, recorder.FlushAsync, shapes.FlushAsync, logs.FlushAsync];

    private ValueTask ReportActive(CancellationToken cancellationToken)
    {
        meter.ReportActive();
        return ValueTask.CompletedTask;
    }

    private async ValueTask SampleAsync(CancellationToken cancellationToken)
    {
        if (!UsageMeter.ValidRegion(options.Region))
        {
            return;
        }

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
            }
        }

        _sampledMinute = minute;
        _cursor = last ?? _cursor;
    }
}

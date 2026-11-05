using System.Diagnostics;
using System.Diagnostics.Metrics;
using TerraBase.Hosting;
using ZLogger;

namespace TerraBase.Features.Backup;

internal sealed class DataKeyRefresh(DataKeys keys, IMeterFactory meters, ILogger<DataKeyRefresh> logger) : BackgroundService
{
    private readonly Meter _meter = meters.Create(Telemetry.MeterName);

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var duration = _meter.CreateHistogram<double>("terrabase.keys.refresh", "ms", "Custody selection refresh time outside local acknowledgement.");
        return Task.WhenAll(Enumerable.Range(0, DataKeys.MaxReads).Select(_ => RunAsync(duration, stoppingToken)));
    }

    private async Task RunAsync(Histogram<double> duration, CancellationToken cancellationToken)
    {
        await foreach (var scope in keys.Refreshes.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            var started = Stopwatch.GetTimestamp();
            var result = "success";
            try
            {
                await keys.RefreshAsync(scope, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                result = "failed";
                logger.ZLogWarning($"Custody selection refresh failed: {error.GetType().Name:@Error}");
            }
            finally
            {
                duration.Record(Stopwatch.GetElapsedTime(started).TotalMilliseconds, new KeyValuePair<string, object?>("result", result));
            }
        }
    }

    public override void Dispose()
    {
        _meter.Dispose();
        base.Dispose();
    }
}

namespace TerraBase.Api.Tests;

/// <summary>Focused coverage for the metric value models and logging seams.</summary>
public sealed class ObservabilityMetricsTests
{
    [Test]
    public async Task LatencyPercentilesPreserveUnitsAndOrdering()
    {
        var latency = LatencyPercentiles.FromMilliseconds(100, 2.5, 9.5, 17.25);

        await Assert.That(latency.SampleCount).IsEqualTo(100);
        await Assert.That(latency.P50).IsEqualTo(TimeSpan.FromMilliseconds(2.5));
        await Assert.That(latency.P95Milliseconds).IsEqualTo(9.5);
        await Assert.That(latency.Get(LatencyPercentile.P99)).IsEqualTo(TimeSpan.FromMilliseconds(17.25));
    }

    [Test]
    public async Task MetricModelsRejectImpossibleValues()
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            Task.FromResult(new LatencyPercentiles(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3), 1)));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            Task.FromResult(new AllocationMetrics(-1, 0)));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            Task.FromResult(new CpuRamMetrics(double.NaN, 0, 0)));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            Task.FromResult(new FanOutMetrics(2, 2, 1)));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            Task.FromResult(new RestoreTimeMetrics(TimeSpan.FromSeconds(-1))));
    }

    [Test]
    public async Task MetricModelsExposeDerivedOperationalValues()
    {
        var allocation = new AllocationMetrics(1_000, 25, 4);
        var amplification = new WriteAmplificationMetrics(1_000, 2_500, 4);
        var fanOut = new FanOutMetrics(5, 3, 1);

        await Assert.That(allocation.AllocatedBytesPerOperation).IsEqualTo(250d);
        await Assert.That(new CpuRamMetrics(37.5, 8_000, 3_000).RamBytes).IsEqualTo(8_000);
        await Assert.That(amplification.Ratio).IsEqualTo(2.5d);
        await Assert.That(fanOut.PendingCount).IsEqualTo(1);
        await Assert.That(new RestoreMetrics(TimeSpan.FromSeconds(3)).RestoreTime).IsEqualTo(TimeSpan.FromSeconds(3));
    }

    [Test]
    public async Task LoggingAdaptersRemainPackageNeutral()
    {
        var adapter = new RecordingAdapter();
        var logger = new AdapterObservabilityLogger(adapter);
        var logEvent = new ObservabilityLogEvent(
            ObservabilityLogLevel.Warning,
            "replication.lag",
            "Replication lag is {lag}",
            new Dictionary<string, object?> { ["lag"] = 12 });

        logger.Log(logEvent);

        await Assert.That(adapter.Event).IsEqualTo(logEvent);
        await Assert.That(new TemplateObservabilityFormatter().Format(logEvent.MessageTemplate, logEvent.Properties))
            .IsEqualTo("Replication lag is 12");
    }

    private sealed class RecordingAdapter : IZLoggerAdapter
    {
        public ObservabilityLogEvent? Event { get; private set; }

        public void Write(ObservabilityLogEvent logEvent) => Event = logEvent;
    }
}

using System.Diagnostics.Metrics;
using System.Globalization;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Features.Memory;
using TerraBase.Hosting;
using TerraBase.Testing;

namespace TerraBase.Fast.Platform;

public sealed class MetricsTests
{
    [Test]
    public async Task Each_server_exports_only_its_own_http_and_feature_measurements()
    {
        await using var first = await TerraBaseTestServer.StartAsync();
        await using var second = await TerraBaseTestServer.StartAsync();
        var firstMeter = first.Services.GetRequiredService<IMeterFactory>().Create(Telemetry.MeterName);
        var secondMeter = second.Services.GetRequiredService<IMeterFactory>().Create(Telemetry.MeterName);
        firstMeter.CreateCounter<long>("terrabase_test_isolation").Add(7);
        secondMeter.CreateCounter<long>("terrabase_test_isolation").Add(11);
        using var health = await first.Client.GetAsync(new Uri("/healthz", UriKind.Relative));
        health.EnsureSuccessStatusCode();
        using var firstReservation = first.Services.GetRequiredService<IMemoryBudget>().TryReserve(MemoryLane.Foreground, 7);
        using var secondReservation = second.Services.GetRequiredService<IMemoryBudget>().TryReserve(MemoryLane.Foreground, 11);
        await Assert.That(firstReservation).IsNotNull();
        await Assert.That(secondReservation).IsNotNull();

        var firstMetrics = await first.Client.GetStringAsync(new Uri("/metrics", UriKind.Relative));
        var secondMetrics = await second.Client.GetStringAsync(new Uri("/metrics", UriKind.Relative));
        await Assert.That(firstMetrics).Contains("http_route=\"/healthz\"");
        await Assert.That(secondMetrics).DoesNotContain("http_route=\"/healthz\"");
        await Assert.That(firstMetrics.Split('\n').Single(line => line.StartsWith("terrabase_test_isolation_total{", StringComparison.Ordinal)).Split(' ')[1])
            .IsEqualTo("7");
        await Assert.That(secondMetrics.Split('\n').Single(line => line.StartsWith("terrabase_test_isolation_total{", StringComparison.Ordinal)).Split(' ')[1])
            .IsEqualTo("11");
        await Assert.That(Sample(firstMetrics, "terrabase_memory_reserved_bytes")).IsEqualTo("7");
        await Assert.That(Sample(secondMetrics, "terrabase_memory_reserved_bytes")).IsEqualTo("11");
    }

    [Test]
    public async Task Runtime_metrics_preserve_their_complete_cpu_and_generation_domains()
    {
        await using var server = await TerraBaseTestServer.StartAsync();
        using var health = await server.Client.GetAsync(new Uri("/healthz", UriKind.Relative));
        health.EnsureSuccessStatusCode();
        var metrics = await server.Client.GetStringAsync(new Uri("/metrics", UriKind.Relative),
            TUnit.Core.TestContext.Current!.Execution.CancellationToken);
        var lines = metrics.Split('\n');
        string[] unlabeled =
        [
            "dotnet_process_memory_working_set_bytes", "dotnet_gc_heap_total_allocated_bytes_total",
            "dotnet_gc_last_collection_memory_committed_size_bytes", "dotnet_gc_pause_time_seconds_total",
            "dotnet_jit_compiled_il_size_bytes_total", "dotnet_jit_compiled_methods_total", "dotnet_jit_compilation_time_seconds_total",
            "dotnet_monitor_lock_contentions_total", "dotnet_thread_pool_thread_count", "dotnet_thread_pool_work_item_count_total",
            "dotnet_thread_pool_queue_length", "dotnet_timer_count", "dotnet_assembly_count", "dotnet_process_cpu_count",
        ];
        foreach (var name in unlabeled)
        {
            var series = lines.Where(line => line.StartsWith(name + "{", StringComparison.Ordinal)).ToArray();
            await Assert.That(series.Length).IsEqualTo(1);
            await Assert.That(series[0]).DoesNotContain("otel_metric_overflow");
        }

        await Assert.That(Sample(metrics, "dotnet_process_cpu_count"))
            .IsEqualTo(Environment.ProcessorCount.ToString(CultureInfo.InvariantCulture));
        await Assert.That(lines.Count(line => line.StartsWith("dotnet_process_cpu_time_seconds_total{", StringComparison.Ordinal)))
            .IsEqualTo(2);
        foreach (var mode in new[] { "user", "system" })
        {
            await Assert.That(Sample(metrics, "dotnet_process_cpu_time_seconds_total", $"cpu_mode=\"{mode}\""))
                .IsNotNullOrEmpty();
        }

        var generations = new[] { "gen0", "gen1", "gen2", "loh", "poh" };
        foreach (var name in new[] { "dotnet_gc_collections_total", "dotnet_gc_last_collection_heap_size_bytes", "dotnet_gc_last_collection_heap_fragmentation_size_bytes" })
        {
            var count = name == "dotnet_gc_collections_total" ? GC.MaxGeneration + 1 : Math.Min(GC.GetGCMemoryInfo().GenerationInfo.Length, generations.Length);
            await Assert.That(lines.Count(line => line.StartsWith(name + "{", StringComparison.Ordinal))).IsEqualTo(count);
            foreach (var generation in generations.Take(count))
            {
                await Assert.That(Sample(metrics, name, $"gc_heap_generation=\"{generation}\""))
                    .IsNotNullOrEmpty();
            }
        }
    }

    [Test]
    public async Task Unlisted_instruments_keep_default_cardinality_and_histogram_buckets()
    {
        await using var server = await TerraBaseTestServer.StartAsync();
        var meter = server.Services.GetRequiredService<IMeterFactory>().Create(Telemetry.MeterName);
        var counter = meter.CreateCounter<long>("terrabase_test_cardinality");
        for (var slot = 0; slot <= 2000; slot++)
        {
            counter.Add(1, new KeyValuePair<string, object?>("slot", slot));
        }

        counter.Add(7, new KeyValuePair<string, object?>("slot", 0));
        var histogram = meter.CreateHistogram<double>("terrabase_test_latency", "s",
            advice: new InstrumentAdvice<double> { HistogramBucketBoundaries = [0.1, 1] });
        histogram.Record(0.05, new KeyValuePair<string, object?>("result", "fast"));
        histogram.Record(2, new KeyValuePair<string, object?>("result", "slow"));

        var metrics = await server.Client.GetStringAsync(new Uri("/metrics", UriKind.Relative),
            TUnit.Core.TestContext.Current!.Execution.CancellationToken);
        var samples = metrics.Split('\n').Where(line => line.StartsWith("terrabase_test_cardinality_total{", StringComparison.Ordinal)).ToArray();
        await Assert.That(samples.Length).IsEqualTo(2001);
        await Assert.That(Sample(metrics, "terrabase_test_cardinality_total", "slot=\"0\"")).IsEqualTo("8");
        await Assert.That(Sample(metrics, "terrabase_test_cardinality_total", "otel_metric_overflow=\"true\"")).IsEqualTo("1");
        await Assert.That(Sample(metrics, "terrabase_test_latency_seconds_count", "result=\"fast\"")).IsEqualTo("1");
        await Assert.That(Sample(metrics, "terrabase_test_latency_seconds_sum", "result=\"fast\"")).IsEqualTo("0.05");
        await Assert.That(Sample(metrics, "terrabase_test_latency_seconds_count", "result=\"slow\"")).IsEqualTo("1");
        await Assert.That(Sample(metrics, "terrabase_test_latency_seconds_sum", "result=\"slow\"")).IsEqualTo("2");
        await Assert.That(Sample(metrics, "terrabase_test_latency_seconds_bucket", "result=\"fast\"", "le=\"0.1\"")).IsEqualTo("1");
        await Assert.That(Sample(metrics, "terrabase_test_latency_seconds_bucket", "result=\"slow\"", "le=\"1\"")).IsEqualTo("0");
        await Assert.That(Sample(metrics, "terrabase_test_latency_seconds_bucket", "result=\"slow\"", "le=\"+Inf\"")).IsEqualTo("1");
    }

    [Test]
    public async Task Metrics_are_exposed_in_prometheus_text_format()
    {
        await using var server = await TerraBaseTestServer.StartAsync();
        // Generate an HTTP server measurement before scraping.
        using var warmup = await server.Client.GetAsync(new Uri("/healthz", UriKind.Relative));

        using var response = await server.Client.GetAsync(new Uri("/metrics", UriKind.Relative));
        var text = await response.Content.ReadAsStringAsync();

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(response.Content.Headers.ContentType?.MediaType).IsEqualTo("text/plain");
        await Assert.That(text).Contains("http_server_request_duration_seconds");
    }

    private static string Sample(string metrics, string name, params string[] labels) =>
        metrics.Split('\n').Single(line => line.StartsWith(name + "{", StringComparison.Ordinal)
            && labels.All(label => line.Contains(label, StringComparison.Ordinal))).Split(' ')[1];
}

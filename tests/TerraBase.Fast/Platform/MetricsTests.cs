using System.Diagnostics.Metrics;
using System.Globalization;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics;
using OpenTelemetry.Metrics;
using TerraBase.Features.Memory;
using TerraBase.Hosting;
using TerraBase.Testing;

namespace TerraBase.Fast.Platform;

public sealed class MetricsTests
{
    [Test]
    public async Task Each_server_exports_only_its_own_http_and_feature_measurements()
    {
        if (Environment.GetEnvironmentVariable(IsolationChildFlag) != "1")
        {
            // Its global meters reach every host in the process: one starting beside it can add an instrument twice
            // (Microsoft.Extensions.Diagnostics race, see A_starting_host_never_sees_the_isolation_tests_global_instruments).
            await IsolatedTestChild.RunAsync(typeof(MetricsTests), nameof(Each_server_exports_only_its_own_http_and_feature_measurements), IsolationChildFlag);
            return;
        }

        var name = "terrabase_test_isolation_" + Guid.NewGuid().ToString("N");
        var foreignPublished = 0;
        await using var first = await TerraBaseTestServer.StartAsync(services: services =>
            services.ConfigureOpenTelemetryMeterProvider((provider, metrics) =>
            {
                var factory = provider.GetRequiredService<IMeterFactory>();
                metrics.AddView(instrument =>
                {
                    if (instrument.Name == name && instrument.Meter.Scope is not null && !ReferenceEquals(instrument.Meter.Scope, factory))
                    {
                        Interlocked.Increment(ref foreignPublished);
                    }

                    return null;
                });
            }));
        await using var second = await TerraBaseTestServer.StartAsync();
        var firstMeter = first.Services.GetRequiredService<IMeterFactory>().Create(Telemetry.MeterName, version: name);
        var secondMeter = second.Services.GetRequiredService<IMeterFactory>().Create(Telemetry.MeterName, version: name);
        firstMeter.CreateCounter<long>(name).Add(7);
        secondMeter.CreateCounter<long>(name).Add(11);
        await Assert.That(Volatile.Read(ref foreignPublished)).IsEqualTo(0);
        using var globalMeter = new Meter(Telemetry.MeterName);
        globalMeter.CreateCounter<long>(name + "_global").Add(13);
        using var lowerCaseMeter = new Meter(Telemetry.MeterName.ToLowerInvariant());
        lowerCaseMeter.CreateCounter<long>(name + "_case").Add(17);
        using var unmatchedMeter = new Meter(Telemetry.MeterName + ".unsubscribed");
        unmatchedMeter.CreateCounter<long>(name + "_unmatched").Add(1);
        using var unmatchedHttpMeter = new Meter("Microsoft.AspNetCore.Hosting.unsubscribed");
        unmatchedHttpMeter.CreateCounter<long>(name + "_unmatched_http").Add(1);
        using var health = await first.Client.GetAsync(new Uri("/healthz", UriKind.Relative));
        health.EnsureSuccessStatusCode();
        // Real background work also reserves the server budget. Give this scenario's real budgets a distinct meter
        // version, so exact feature-isolation assertions cannot accidentally count that unrelated work (#869/#870).
        using var backgroundFirst = first.Services.GetRequiredService<IMemoryBudget>().TryReserve(MemoryLane.Foreground, 3164);
        using var backgroundSecond = second.Services.GetRequiredService<IMemoryBudget>().TryReserve(MemoryLane.Foreground, 5186);
        var firstBudget = new MemoryBudget(1024, firstMeter);
        var secondBudget = new MemoryBudget(1024, secondMeter);
        using var firstReservation = firstBudget.TryReserve(MemoryLane.Foreground, 7);
        using var secondReservation = secondBudget.TryReserve(MemoryLane.Foreground, 11);
        await Assert.That(backgroundFirst).IsNotNull();
        await Assert.That(backgroundSecond).IsNotNull();
        await Assert.That(firstReservation).IsNotNull();
        await Assert.That(secondReservation).IsNotNull();

        var firstMetrics = await first.Client.GetStringAsync(new Uri("/metrics", UriKind.Relative));
        var secondMetrics = await second.Client.GetStringAsync(new Uri("/metrics", UriKind.Relative));
        await Assert.That(firstMetrics).Contains("http_route=\"/healthz\"");
        await Assert.That(secondMetrics).DoesNotContain("http_route=\"/healthz\"");
        await Assert.That(firstMetrics.Split('\n').Single(line => line.StartsWith(name + "_total{", StringComparison.Ordinal)).Split(' ')[1])
            .IsEqualTo("7");
        await Assert.That(secondMetrics.Split('\n').Single(line => line.StartsWith(name + "_total{", StringComparison.Ordinal)).Split(' ')[1])
            .IsEqualTo("11");
        foreach (var metrics in new[] { firstMetrics, secondMetrics })
        {
            await Assert.That(Sample(metrics, name + "_global_total")).IsEqualTo("13");
            await Assert.That(Sample(metrics, name + "_case_total")).IsEqualTo("17");
            await Assert.That(metrics).DoesNotContain(name + "_unmatched");
        }
        await Assert.That(Sample(firstMetrics, "terrabase_memory_reserved_bytes", $"otel_scope_version=\"{name}\"")).IsEqualTo("7");
        await Assert.That(Sample(secondMetrics, "terrabase_memory_reserved_bytes", $"otel_scope_version=\"{name}\"")).IsEqualTo("11");
    }

    /// <summary>
    /// A host's metrics subscription applies its rules through a temporary listener that adds instruments without its lock
    /// (Microsoft.Extensions.Diagnostics ListenerSubscription.UpdateRules); a matching global instrument published then is
    /// added again by Initialize, which throws "An item with the same key has already been added" (CI run 38259616461).
    /// This holds a host inside that window while the global-meter test runs: none of its instruments may reach the host.
    /// </summary>
    [Test]
    public async Task A_starting_host_never_sees_the_isolation_tests_global_instruments()
    {
        using var seedMeter = new Meter("TerraBase.Fast.MetricsGate." + Guid.NewGuid().ToString("N"));
        seedMeter.CreateCounter<long>("seed").Add(1);
        using var gate = new RulesWindowGate(seedMeter.Name);
        var starting = Task.Run(() => TerraBaseTestServer.StartAsync(services: services => services.AddMetrics(metrics => metrics
            .AddListener(gate)
            .EnableMetrics(seedMeter.Name, listenerName: gate.Name)
            .EnableMetrics(Telemetry.MeterName, listenerName: gate.Name)
            .EnableMetrics("Microsoft.AspNetCore.Hosting", listenerName: gate.Name))));
        try
        {
            await gate.Held.Task.WaitAsync(TUnit.Core.TestContext.Current!.Execution.CancellationToken);
            await Each_server_exports_only_its_own_http_and_feature_measurements();
            await Assert.That(gate.GlobalInstrumentsSeen).IsEqualTo(0);
        }
        finally
        {
            gate.Release.Set();
            await using var server = await starting;
        }
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

        // Kestrel records the request duration after the response is sent: a scrape can arrive before it.
        await TerraBaseTestServer.EventuallyAsync(async token =>
        {
            using var response = await server.Client.GetAsync(new Uri("/metrics", UriKind.Relative), token);
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(response.Content.Headers.ContentType?.MediaType).IsEqualTo("text/plain");
            return (await response.Content.ReadAsStringAsync(token)).Contains("http_server_request_duration_seconds", StringComparison.Ordinal);
        });
    }

    private const string IsolationChildFlag = "TERRABASE_TEST_METRICS_ISOLATION_CHILD";

    // Holds the host's rules pass on its first (seed) instrument and counts the global instruments it is offered meanwhile.
    private sealed class RulesWindowGate(string seedMeter) : IMetricsListener, IDisposable
    {
        private int _globalInstruments;
        private int _seeds;

        public TaskCompletionSource Held { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ManualResetEventSlim Release { get; } = new();

        public int GlobalInstrumentsSeen => Volatile.Read(ref _globalInstruments);

        public string Name => "rules-window-gate";

        public void Initialize(IObservableInstrumentsSource source)
        {
        }

        public bool InstrumentPublished(Instrument instrument, out object? userState)
        {
            userState = null;
            if (instrument.Meter.Name == seedMeter)
            {
                if (Interlocked.Increment(ref _seeds) == 1)
                {
                    Held.SetResult();
                    Release.Wait();
                }
            }
            else if (instrument.Meter.Scope is null)
            {
                Interlocked.Increment(ref _globalInstruments);
            }

            return true;
        }

        public void MeasurementsCompleted(Instrument instrument, object? userState)
        {
        }

        public MeasurementHandlers GetMeasurementHandlers() => new();

        public void Dispose() => Release.Dispose();
    }

    private static string Sample(string metrics, string name, params string[] labels) =>
        metrics.Split('\n').Single(line => line.StartsWith(name + "{", StringComparison.Ordinal)
            && labels.All(label => line.Contains(label, StringComparison.Ordinal))).Split(' ')[1];
}

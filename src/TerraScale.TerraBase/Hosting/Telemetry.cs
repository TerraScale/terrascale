using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;
using OpenTelemetry;
using OpenTelemetry.Metrics;

namespace TerraBase.Hosting;

/// <summary>Prometheus-compatible metrics for the process and its features.</summary>
public static class Telemetry
{
    /// <summary>
    /// Meter that every TerraBase feature records its instruments on. It keeps its name across the TerraBase
    /// rename (TS-589): Prometheus series carry it as <c>otel_scope_name</c>.
    /// </summary>
    public const string MeterName = "TerraScale.TerraBase";

    /// <summary>Path of the Prometheus scrape endpoint on the administrative listener.</summary>
    public const string MetricsPath = "/metrics";

    /// <summary>Registers runtime, HTTP server and TerraBase meters.</summary>
    public static IServiceCollection AddTerraBaseTelemetry(this IServiceCollection services)
    {
        // SDK self-diagnostics lives for the process; its worker must not retain the first host's context.
        using (ExecutionContext.SuppressFlow())
        {
            RuntimeHelpers.RunClassConstructor(typeof(Sdk).TypeHandle);
        }

        services.AddOpenTelemetry().WithMetrics(metrics => metrics
            .AddMeter(MeterName)
            .AddAspNetCoreInstrumentation()
            .AddRuntimeInstrumentation()
            .AddPrometheusExporter());
        // Meters are matched by name across the whole process, so a process with several hosts (the
        // integration suites) would report every host's instruments. Keep those created through this
        // host's IMeterFactory and the process-wide ones (no scope), such as the runtime's.
        services.ConfigureOpenTelemetryMeterProvider((provider, metrics) =>
        {
            var meters = provider.GetRequiredService<IMeterFactory>();
            metrics.AddView(instrument =>
            {
                if (instrument.Meter.Scope is not null && !ReferenceEquals(instrument.Meter.Scope, meters))
                {
                    return MetricStreamConfiguration.Drop;
                }

                if (instrument.Meter.Name != MeterName)
                {
                    return null;
                }

                // The SDK eagerly allocates 2,002 points per stream by default, including unlabeled gauges.
                // Only known unlabeled instruments need one series; other instruments retain the SDK defaults.
                // Adding labels to an instrument requires removing it from this list.
                return instrument.Name switch
                {
                    "terrabase_pressure_cpu_util" or "terrabase_pressure_mem_util"
                        or "terrabase_pressure_memory_used_bytes" or "terrabase_pressure_memory_budget_bytes"
                        or "terrabase_pressure_disk_used_bytes" or "terrabase_pressure_disk_free_bytes"
                        or "terrabase_pressure_disk_total_bytes" or "terrabase_pressure_request_rate"
                        or "terrabase_pressure_p95_latency_ms" or "terrabase_pressure_data_bytes"
                        or "terrabase_pressure_write_qps" or "terrabase_pressure_connections"
                        or "terrabase_pressure_sampled_at_seconds" or "terrabase_pressure_storage_sampled_at_seconds"
                        or "terrabase_pressure_sample_window_seconds" or "terrabase_pressure_untracked_tenant_requests"
                        or "terrabase.memory.reserved"
                        or "terrabase.storage.admitted" or "terrabase.storage.running"
                        or "terrabase.storage.rejected" or "terrabase.storage.unknown_files"
                        or "terrabase.document_cache.bytes" or "terrabase.document_cache.entries"
                        or "terrabase.document_cache.promotions"
                        or "terrabase.shape.reads" or "terrabase.shape.requests.waiting"
                        or "terrabase.shape.flights.active" or "terrabase.shape.live.waits"
                        or "terrabase.subscriptions.active"
                        or "terrabase.catalog.follower.connected" or "terrabase.catalog.follower.lag"
                        or "terrabase.catalog.applied_sequence"
                        or "terrabase.backup.failures" or "terrabase.backup.holes" or "terrabase.backup.generations"
                        or "terrabase.backup.stored" or "terrabase.backup.files" or "terrabase.backup.lag"
                        or "terrabase.backup.last_success"
                        or "terrabase.replication.sent.groups" or "terrabase.replication.sent.plain"
                        or "terrabase.replication.sent.encoded" or "terrabase.replication.sender.prefix.mismatches"
                        or "terrabase.replication.sender.discovery.passes" or "terrabase.replication.sender.credit.waits"
                        or "terrabase.replication.sender.durable_lag" or "terrabase.replication.sender.initialized"
                        or "terrabase.replication.observability.omitted" or "terrabase.wan.served.groups"
                        => new MetricStreamConfiguration { CardinalityLimit = 1 },
                    _ => null,
                };
            });
        });
        return services;
    }

    /// <summary>Maps the Prometheus scrape endpoint.</summary>
    public static IEndpointRouteBuilder MapTerraBaseMetrics(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPrometheusScrapingEndpoint(MetricsPath);
        return endpoints;
    }
}

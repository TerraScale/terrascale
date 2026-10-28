using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Diagnostics.Metrics;
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

    // The built-in meters enabled by OpenTelemetry's ASP.NET Core instrumentation on .NET 11.
    private static readonly string[] AspNetCoreMeters =
    [
        "Microsoft.AspNetCore.Hosting", "Microsoft.AspNetCore.Server.Kestrel", "Microsoft.AspNetCore.Http.Connections",
        "Microsoft.AspNetCore.Routing", "Microsoft.AspNetCore.Diagnostics", "Microsoft.AspNetCore.RateLimiting",
        "Microsoft.AspNetCore.Components", "Microsoft.AspNetCore.Components.Server.Circuits", "Microsoft.AspNetCore.Components.Lifecycle",
        "Microsoft.AspNetCore.Authorization", "Microsoft.AspNetCore.Authentication", "Microsoft.AspNetCore.Identity",
        "Microsoft.AspNetCore.MemoryPool",
    ];

    /// <summary>Registers runtime, HTTP server and TerraBase meters.</summary>
    public static IServiceCollection AddTerraBaseTelemetry(this IServiceCollection services)
    {
        // SDK self-diagnostics lives for the process; its worker must not retain the first host's context.
        using (ExecutionContext.SuppressFlow())
        {
            RuntimeHelpers.RunClassConstructor(typeof(Sdk).TypeHandle);
        }

        services.AddOpenTelemetry().WithMetrics(metrics => metrics
            .AddRuntimeInstrumentation()
            .AddPrometheusExporter());
        // Native subscriptions reject foreign host scopes before the SDK allocates publishing state.
        services.AddMetrics(metrics =>
        {
            metrics.EnableMetrics(MeterName, listenerName: "OpenTelemetry");
            foreach (var name in AspNetCoreMeters)
            {
                metrics.EnableMetrics(name, listenerName: "OpenTelemetry");
            }
        });
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

                // The runtime defines these complete domains; exception types and unknown instruments keep defaults.
                if (instrument.Meter.Name == "System.Runtime")
                {
                    return instrument.Name switch
                    {
                        "dotnet.process.memory.working_set" or "dotnet.gc.heap.total_allocated"
                            or "dotnet.gc.last_collection.memory.committed_size" or "dotnet.gc.pause.time"
                            or "dotnet.jit.compiled_il.size" or "dotnet.jit.compiled_methods" or "dotnet.jit.compilation.time"
                            or "dotnet.monitor.lock_contentions" or "dotnet.thread_pool.thread.count"
                            or "dotnet.thread_pool.work_item.count" or "dotnet.thread_pool.queue.length"
                            or "dotnet.timer.count" or "dotnet.assembly.count" or "dotnet.process.cpu.count"
                            => new MetricStreamConfiguration { CardinalityLimit = 1 },
                        "dotnet.process.cpu.time" => new MetricStreamConfiguration { CardinalityLimit = 2 },
                        "dotnet.gc.collections" or "dotnet.gc.last_collection.heap.size"
                            or "dotnet.gc.last_collection.heap.fragmentation.size"
                            => new MetricStreamConfiguration { CardinalityLimit = 5 },
                        _ => null,
                    };
                }

                if (instrument.Meter.Name != MeterName)
                {
                    // Native meter rules match prefixes; retain the original exact-name subscriptions.
                    return string.Equals(instrument.Meter.Name, MeterName, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(instrument.Meter.Name, "System.Runtime", StringComparison.OrdinalIgnoreCase)
                        || AspNetCoreMeters.Contains(instrument.Meter.Name, StringComparer.OrdinalIgnoreCase)
                        ? null : MetricStreamConfiguration.Drop;
                }

                // The SDK eagerly allocates 2,002 points per stream by default, including unlabeled gauges.
                // Known streams need only their complete label domain; other streams retain the SDK defaults.
                // Adding a label or value requires increasing the limit or removing the instrument from this list.
                return instrument.Name switch
                {
                    "terrabase_peer_topology_mismatch" or "terrabase_peer_topology_generation_age_seconds"
                        or "terrabase_pressure_cpu_util" or "terrabase_pressure_mem_util"
                        or "terrabase_pressure_memory_used_bytes" or "terrabase_pressure_memory_budget_bytes"
                        or "terrabase_pressure_disk_used_bytes" or "terrabase_pressure_disk_free_bytes"
                        or "terrabase_pressure_disk_total_bytes" or "terrabase_pressure_request_rate"
                        or "terrabase_pressure_p95_latency_ms" or "terrabase_pressure_data_bytes"
                        or "terrabase_pressure_write_qps" or "terrabase_pressure_connections"
                        or "terrabase_pressure_io_admitted_count" or "terrabase_pressure_sender_durable_lag_seq"
                        or "terrabase_pressure_local_ready"
                        or "terrabase_pressure_sampled_at_seconds" or "terrabase_pressure_storage_sampled_at_seconds"
                        or "terrabase_pressure_sample_window_seconds" or "terrabase_pressure_untracked_tenant_requests"
                        or "terrabase_pressure_info"
                        or "terrabase.memory.reserved" or "terrabase.stats.databases"
                        or "terrabase.catalog.anchor.timeouts"
                        or "terrabase.storage.admitted" or "terrabase.storage.running"
                        or "terrabase.storage.rejected" or "terrabase.storage.unknown_files"
                        or "terrabase.document_cache.bytes" or "terrabase.document_cache.entries"
                        or "terrabase.document_cache.promotions"
                        or "terrabase.backup.failures" or "terrabase.backup.holes" or "terrabase.backup.generations"
                        or "terrabase.backup.stored" or "terrabase.backup.files" or "terrabase.backup.lag"
                        or "terrabase.backup.last_success" or "terrabase.backup.bucket_snapshot"
                        or "terrabase.replication.sent.groups" or "terrabase.replication.sent.plain"
                        or "terrabase.replication.sent.encoded" or "terrabase.replication.sender.prefix.mismatches"
                        or "terrabase.replication.sender.discovery.passes" or "terrabase.replication.sender.credit.waits"
                        or "terrabase.replication.sender.durable_lag" or "terrabase.replication.sender.initialized"
                        or "terrabase.replication.observability.omitted" or "terrabase.wan.served.groups"
                        or "terrabase.replication.cdc_backlog" or "terrabase.replication.tail_lag"
                        or "terrabase.shape.reads" or "terrabase.shape.requests.waiting" or "terrabase.shape.flights.active"
                        or "terrabase.shape.seals" or "terrabase.shape.seals.duration" or "terrabase.shape.sealed_bytes"
                        or "terrabase.shape.mirror.lag" or "terrabase.shape.redirects"
                        => new MetricStreamConfiguration { CardinalityLimit = 1 },
                    "terrabase.auth.key_fetches" or "terrabase.stats.operations" => new MetricStreamConfiguration { CardinalityLimit = 2 },
                    "terrabase_pressure_listener_request_rate" or "terrabase_pressure_listener_p95_latency_ms"
                        or "terrabase_pressure_listener_requests" or "terrabase.document_cache.evictions" or "terrabase.stats.dropped"
                        or "terrabase.stats.rollups"
                        => new MetricStreamConfiguration { CardinalityLimit = 3 },
                    "terrabase.document_cache.lookups" => new MetricStreamConfiguration { CardinalityLimit = 4 },
                    // Five bounded storage paths plus the exporter's reserved overflow series.
                    "terrabase.storage.reads" => new MetricStreamConfiguration { CardinalityLimit = 6 },
                    "terrabase.query_shapes.dropped" => new MetricStreamConfiguration { CardinalityLimit = 7 },
                    // Nine creation stages by four outcomes plus the exporter's reserved overflow series.
                    "terrabase.database_create.stage_duration" => new MetricStreamConfiguration { CardinalityLimit = 37 },
                    "terrabase_pressure_top_tenant_request_rate" or "terrabase_pressure_top_tenant_storage_bytes"
                        or "terrabase.stats.top.replication" or "terrabase.stats.top.operations"
                        => new MetricStreamConfiguration { CardinalityLimit = 10 },
                    // One series per peer region (docs/terrabase/multi-region-replication.md, up to 20 regions) and "unknown".
                    "terrabase.replication.send_lag" or "terrabase.replication.ack_lag" or "terrabase.replication.merged_groups"
                        or "terrabase.replication.apply_delay" or "terrabase.replication.repairs" or "terrabase.replication.unreadable_reads"
                        => new MetricStreamConfiguration { CardinalityLimit = 32 },
                    "terrabase.stats.replication" => new MetricStreamConfiguration { CardinalityLimit = 129 },
                    "terrabase_pressure_available" => new MetricStreamConfiguration { CardinalityLimit = 13 },
                    _ => null,
                };
            });
        });
        return services;
    }

    /// <summary>Maps the Prometheus scrape endpoint.</summary>
    public static IEndpointRouteBuilder MapTerraBaseMetrics(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPrometheusScrapingEndpoint(MetricsPath).WithMetadata(ListenerMetadata.Metrics);
        return endpoints;
    }
}

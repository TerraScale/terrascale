using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Diagnostics.Metrics;
using OpenTelemetry;
using OpenTelemetry.Metrics;

namespace TerraBase.Hosting;

public static class Telemetry
{
    public const string MeterName = "TerraScale.TerraBase";
    public const string MetricsPath = "/metrics";
    private static readonly string[] AspNetCoreMeters =
    [
        "Microsoft.AspNetCore.Hosting", "Microsoft.AspNetCore.Server.Kestrel", "Microsoft.AspNetCore.Http.Connections",
        "Microsoft.AspNetCore.Routing", "Microsoft.AspNetCore.Diagnostics", "Microsoft.AspNetCore.RateLimiting",
        "Microsoft.AspNetCore.Components", "Microsoft.AspNetCore.Components.Server.Circuits", "Microsoft.AspNetCore.Components.Lifecycle",
        "Microsoft.AspNetCore.Authorization", "Microsoft.AspNetCore.Authentication", "Microsoft.AspNetCore.Identity",
        "Microsoft.AspNetCore.MemoryPool",
    ];
    public static IServiceCollection AddTerraBaseTelemetry(this IServiceCollection services)
    {
        using (ExecutionContext.SuppressFlow())
        {
            RuntimeHelpers.RunClassConstructor(typeof(Sdk).TypeHandle);
        }

        services.AddOpenTelemetry().WithMetrics(metrics => metrics
            .AddRuntimeInstrumentation()
            .AddPrometheusExporter());
        services.AddMetrics(metrics =>
        {
            metrics.EnableMetrics(MeterName, listenerName: "OpenTelemetry");
            foreach (var name in AspNetCoreMeters)
            {
                metrics.EnableMetrics(name, listenerName: "OpenTelemetry");
            }
        });
        services.ConfigureOpenTelemetryMeterProvider((provider, metrics) =>
        {
            var meters = provider.GetRequiredService<IMeterFactory>();
            metrics.AddView(instrument =>
            {
                if (instrument.Meter.Scope is not null && !ReferenceEquals(instrument.Meter.Scope, meters))
                {
                    return MetricStreamConfiguration.Drop;
                }
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
                    return string.Equals(instrument.Meter.Name, MeterName, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(instrument.Meter.Name, "System.Runtime", StringComparison.OrdinalIgnoreCase)
                        || AspNetCoreMeters.Contains(instrument.Meter.Name, StringComparer.OrdinalIgnoreCase)
                        ? null : MetricStreamConfiguration.Drop;
                }
                return instrument.Name switch
                {
                    "terrabase_pressure_cpu_util" or "terrabase_pressure_mem_util"
                        or "terrabase_pressure_memory_used_bytes" or "terrabase_pressure_memory_budget_bytes"
                        or "terrabase_pressure_disk_used_bytes" or "terrabase_pressure_disk_free_bytes"
                        or "terrabase_pressure_disk_total_bytes" or "terrabase_pressure_request_rate"
                        or "terrabase_pressure_p95_latency_ms" or "terrabase_pressure_data_bytes"
                        or "terrabase_pressure_write_qps" or "terrabase_pressure_connections"
                        or "terrabase_pressure_io_admitted_count"
                        or "terrabase_pressure_local_ready"
                        or "terrabase_pressure_sampled_at_seconds" or "terrabase_pressure_storage_sampled_at_seconds"
                        or "terrabase_pressure_sample_window_seconds" or "terrabase_pressure_untracked_tenant_requests"
                        or "terrabase_pressure_info"
                        or "terrabase.memory.reserved" or "terrabase.stats.databases"
                        or "terrabase.catalog.verified"
                        or "terrabase.storage.admitted" or "terrabase.storage.running"
                        or "terrabase.storage.rejected" or "terrabase.storage.unknown_files"
                        or "terrabase.document_cache.bytes" or "terrabase.document_cache.entries"
                        or "terrabase.document_cache.promotions"
                        or "terrabase.backup.failures" or "terrabase.backup.holes" or "terrabase.backup.generations"
                        or "terrabase.backup.stored" or "terrabase.backup.files" or "terrabase.backup.lag"
                        or "terrabase.backup.last_success" or "terrabase.backup.bucket_snapshot"
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
                    "terrabase.storage.reads" => new MetricStreamConfiguration { CardinalityLimit = 6 },
                    "terrabase.query_shapes.dropped" => new MetricStreamConfiguration { CardinalityLimit = 7 },
                    "terrabase_pressure_top_tenant_request_rate" or "terrabase_pressure_top_tenant_storage_bytes"
                        or "terrabase.stats.top.operations"
                        => new MetricStreamConfiguration { CardinalityLimit = 10 },
                    "terrabase_pressure_available" => new MetricStreamConfiguration { CardinalityLimit = 11 },
                    _ => null,
                };
            });
        });
        return services;
    }
    public static IEndpointRouteBuilder MapTerraBaseMetrics(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPrometheusScrapingEndpoint(MetricsPath).WithMetadata(ListenerMetadata.Metrics);
        return endpoints;
    }
}

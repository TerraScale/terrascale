using System.Diagnostics.Metrics;
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
                instrument.Meter.Scope is null || ReferenceEquals(instrument.Meter.Scope, meters) ? null : MetricStreamConfiguration.Drop);
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

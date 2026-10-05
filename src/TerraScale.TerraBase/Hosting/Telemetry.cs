using System.Diagnostics.Metrics;
using OpenTelemetry.Metrics;

namespace TerraScale.TerraBase.Hosting;

/// <summary>Prometheus-compatible metrics for the process and its features.</summary>
public static class Telemetry
{
    /// <summary>Meter that every TerraBase feature records its instruments on.</summary>
    public const string MeterName = "TerraScale.TerraBase";

    /// <summary>Path of the Prometheus scrape endpoint on the administrative listener.</summary>
    public const string MetricsPath = "/metrics";

    /// <summary>Registers runtime, HTTP server and TerraBase meters.</summary>
    public static IServiceCollection AddTerraBaseTelemetry(this IServiceCollection services)
    {
        services.ConfigureOpenTelemetryMeterProvider((provider, builder) =>
        {
            var meters = provider.GetRequiredService<IMeterFactory>();
            // Scoped meters belong to one host; runtime meters remain process-wide.
            builder.AddView(instrument => instrument.Meter.Scope is { } scope && !ReferenceEquals(scope, meters)
                ? MetricStreamConfiguration.Drop
                : null);
        });
        services.AddOpenTelemetry().WithMetrics(metrics => metrics
            .AddMeter(MeterName)
            .AddAspNetCoreInstrumentation()
            .AddRuntimeInstrumentation()
            .AddPrometheusExporter());
        return services;
    }

    /// <summary>Maps the Prometheus scrape endpoint.</summary>
    public static IEndpointRouteBuilder MapTerraBaseMetrics(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPrometheusScrapingEndpoint(MetricsPath);
        return endpoints;
    }
}

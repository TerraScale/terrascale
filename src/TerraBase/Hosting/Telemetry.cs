using System.Reflection;
using Microsoft.Extensions.Diagnostics.Metrics;
using OpenTelemetry.Metrics;

namespace TerraBase.Hosting;

public static class Telemetry
{
    public const string MeterName = "TerraScale.TerraBase";

    public const string MetricsPath = "/metrics";

    private static readonly string[] Meters =
    [
        MeterName, "TerraScale.QuicRpc", "Microsoft.AspNetCore.Hosting", "Microsoft.AspNetCore.Server.Kestrel", "Microsoft.AspNetCore.Routing",
        "Microsoft.AspNetCore.Diagnostics", "Microsoft.AspNetCore.Authentication",
    ];

    public static IServiceCollection AddTerraBaseTelemetry(this IServiceCollection services)
    {
        services.AddOpenTelemetry().WithMetrics(metrics => metrics
            .AddRuntimeInstrumentation()
            .AddPrometheusExporter(options => options.ScrapeResponseCacheDurationMilliseconds = 0));
        services.AddMetrics(metrics =>
        {
            foreach (var name in Meters)
            {
                metrics.EnableMetrics(name, listenerName: "OpenTelemetry");
            }
        });
        return services;
    }

    public static WebApplication MapTerraBaseMetrics(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.MapPrometheusScrapingEndpoint(MetricsPath).WithMetadata(ListenerMetadata.Metrics);
        return app;
    }
}

public static class ServiceVersion
{
    public static string Value { get; } = Informational.Split('+')[0];

    public static string? Revision { get; } = Informational.Split('+') is [_, var revision, ..] && revision.Length > 0 ? revision : null;

    private static string Informational =>
        typeof(ServiceVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
}

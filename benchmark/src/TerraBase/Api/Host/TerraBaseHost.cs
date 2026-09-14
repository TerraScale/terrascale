// <copyright file="TerraBaseHost.cs" company="TerraScale">
// TerraBase (SE-08). See LICENSE.md.
// </copyright>

namespace TerraBase.Api;

using System.Net;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using TerraScale.Endpoints;

/// <summary>Builds the loopback observability host.</summary>
public static class TerraBaseHost
{
    /// <summary>Builds the application with process-local metrics and health probes.</summary>
    /// <param name="listenUrl">A single loopback HTTP or HTTPS URL.</param>
    /// <param name="configure">An optional builder hook.</param>
    /// <returns>The application.</returns>
    public static WebApplication Create(
        string listenUrl = "http://127.0.0.1:8420",
        Action<WebApplicationBuilder>? configure = null)
    {
        ValidateListenUrl(listenUrl);
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls(listenUrl);
        configure?.Invoke(builder);
        ConfigureObservability(builder);

        builder.Services.AddSingleton<MetricsRegistry>();
        builder.Services.AddSingleton<IMetricsSource>(services => services.GetRequiredService<MetricsRegistry>());
        builder.Services.AddTerraScaleEndpoints(typeof(TerraBaseHost).Assembly, endpointOptions =>
        {
            endpointOptions.Title = "TerraBase Observability";
            endpointOptions.Version = "v1";
        });
        builder.Services.ConfigureHttpJsonOptions(json =>
        {
            json.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
        });

        var app = builder.Build();
        app.MapTerraScaleEndpoints();
        app.MapTerraScaleOpenApi();
        return app;
    }

    private static void ConfigureObservability(WebApplicationBuilder builder)
    {
        builder.Logging.AddSentry(options =>
        {
            options.Dsn = builder.Configuration["Sentry:Dsn"]
                ?? builder.Configuration["SENTRY_DSN"];
            options.InitializeSdk = !string.IsNullOrWhiteSpace(options.Dsn);
        });

        var telemetry = builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService("terrabase"))
            .WithTracing(tracing => tracing
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation())
            .WithMetrics(metrics => metrics
                .AddAspNetCoreInstrumentation()
                .AddRuntimeInstrumentation());

        var endpoint = builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"];
        if (Uri.TryCreate(endpoint, UriKind.Absolute, out var otlpEndpoint))
        {
            telemetry.WithTracing(tracing => tracing.AddOtlpExporter(exporter => exporter.Endpoint = otlpEndpoint));
            telemetry.WithMetrics(metrics => metrics.AddOtlpExporter(exporter => exporter.Endpoint = otlpEndpoint));
        }
    }

    private static void ValidateListenUrl(string listenUrl)
    {
        if (string.IsNullOrWhiteSpace(listenUrl)
            || listenUrl.Contains(';', StringComparison.Ordinal)
            || !Uri.TryCreate(listenUrl, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https")
            || !IsLoopbackHost(uri.Host))
        {
            throw new ArgumentException(
                "ListenUrl must be one HTTP or HTTPS URL bound to a loopback address.",
                nameof(listenUrl));
        }
    }

    private static bool IsLoopbackHost(string host) =>
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
        || IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address);
}

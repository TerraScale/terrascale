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

/// <summary>
/// The API host assembly point: builds the ASP.NET Core application
/// over one supervised engine endpoint. The production entry point
/// (<see cref="Program"/>) and test host share it, so the tested
/// surface is the served surface.
/// </summary>
public static class TerraBaseHost
{
    /// <summary>Builds the host application over one engine endpoint.</summary>
    /// <param name="options">The engine endpoint and restart policy.</param>
    /// <param name="configure">An optional builder hook (test wiring).</param>
    /// <returns>The application with its supervised engine, stream hub, and audit log.</returns>
    public static HostBundle Create(EngineOptions options, Action<WebApplicationBuilder>? configure = null)
    {
        ValidateListenUrl(options.ListenUrl);
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls(options.ListenUrl);
        configure?.Invoke(builder);

        // Configuration is available from appsettings.json, environment
        // variables, and command-line providers. Bind the supplied engine
        // options as the validated default for consumers resolved from DI.
        builder.Services.AddSingleton(options);
        builder.Services.AddOptions<EngineOptions>()
            .Bind(builder.Configuration.GetSection("TerraBase:Engine"))
            .ValidateDataAnnotations();
        builder.Services.AddMemoryCache();
        builder.Services.AddHttpClient("terrabase")
            .AddStandardResilienceHandler();
        builder.Services.AddServiceDiscovery();
        ConfigureObservability(builder);

        var audit = new AuditLog();
        var supervisor = new EngineSupervisor(options, audit);
        var hub = new BackboneHub(supervisor, audit);
        builder.Services.AddSingleton(new MetricsRegistry());
        builder.Services.AddSingleton(new TypedApi(supervisor, hub, audit));
        builder.Services.AddTerraScaleEndpoints(typeof(TerraBaseHost).Assembly, endpointOptions =>
        {
            endpointOptions.Title = "TerraBase API";
            endpointOptions.Version = "v1";
        });
        builder.Services.ConfigureHttpJsonOptions(json =>
        {
            json.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
        });

        var app = builder.Build();
        app.UseWebSockets();
        app.MapTerraScaleEndpoints();
        app.MapTerraScaleOpenApi();
        return new HostBundle(app, supervisor, hub, audit);
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

/// <summary>The built host: the application and its engine-facing halves.</summary>
/// <param name="App">The ASP.NET Core application.</param>
/// <param name="Supervisor">The engine supervisor.</param>
/// <param name="Hub">The backbone stream hub.</param>
/// <param name="Audit">The lifecycle audit log.</param>
public sealed record HostBundle(WebApplication App, EngineSupervisor Supervisor, BackboneHub Hub, AuditLog Audit);

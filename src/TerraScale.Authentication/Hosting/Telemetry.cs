using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Sentry.Extensibility;
using Sentry.Protocol;
using TerraScale.Authentication.Configuration;
using TerraScale.Logging;

namespace TerraScale.Authentication.Hosting;

public static partial class Telemetry
{
    public const string ServiceName = "ts-auth";

    public const string MetricsPath = "/metrics";

    private static readonly object MetricsConnection = new();

    private static readonly HashSet<string> SafeHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Content-Length",
    };

    private static bool OtlpConfigured(string signal) =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT"))
        || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable($"OTEL_EXPORTER_OTLP_{signal}_ENDPOINT"));

    public static WebApplicationBuilder AddAuthTelemetry(this WebApplicationBuilder builder, AuthOptions options)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(options);

        builder.Logging.AddTerraScaleLogging("ts-auth", ("OpenIddict", LogLevel.Warning), ("Microsoft", LogLevel.Warning));

        builder.Services.AddSafeLogging();

        var traces = OtlpConfigured("TRACES");
        var logs = OtlpConfigured("LOGS");
        if (logs)
        {
            builder.Logging.AddOpenTelemetry(logging =>
            {
                logging.IncludeFormattedMessage = true;
                logging.IncludeScopes = false;
                logging.SetResourceBuilder(ResourceBuilder.CreateDefault().AddService(ServiceName, serviceVersion: ServiceVersion.Value));
                logging.AddOtlpExporter();
            });
        }

        var telemetry = builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(ServiceName, serviceVersion: ServiceVersion.Value))
            .WithMetrics(meters => meters
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation()
                .AddMeter(AuthMetrics.MeterName)
                .AddPrometheusExporter());
        if (traces)
        {
            telemetry.WithTracing(tracing => tracing
                .AddAspNetCoreInstrumentation(aspnet =>
                    aspnet.Filter = context => !context.Request.Path.StartsWithSegments("/healthz", StringComparison.Ordinal)
                        && context.Request.Path.Value?.EndsWith("/attack-protection/unlock", StringComparison.Ordinal) != true)
                .AddHttpClientInstrumentation(http => http.FilterHttpRequestMessage = request => request.RequestUri?.Host != "api.pwnedpasswords.com")
                .AddOtlpExporter());
        }

        if (options.SentryDsn is { } dsn)
        {
            builder.WebHost.UseSentry(sentry =>
            {
                sentry.Dsn = dsn;
                sentry.Release = ServiceVersion.Value;
                sentry.Environment = options.Development ? "development" : "production";
                sentry.SendDefaultPii = false;
                sentry.MaxRequestBodySize = RequestSize.None;
                sentry.SendClientReports = false;
                sentry.AutoSessionTracking = false;
                sentry.TracesSampleRate = 0;
                sentry.MinimumEventLevel = LogLevel.None;
                sentry.MaxBreadcrumbs = 0;
                sentry.SetBeforeSend((sentryEvent, _) => Scrub(sentryEvent));
            });
        }

        return builder;
    }


    public static SentryEvent Scrub(SentryEvent sentryEvent)
    {
        ArgumentNullException.ThrowIfNull(sentryEvent);

        var clean = new SentryEvent
        {
            Level = sentryEvent.Level,
            Release = sentryEvent.Release,
            Environment = sentryEvent.Environment,
            Platform = sentryEvent.Platform,
            SentryExceptions = (sentryEvent.SentryExceptions ?? []).Select(ScrubException).ToList(),
        };

        clean.Request.Method = sentryEvent.Request.Method;
        clean.Request.Url = PathOnly(sentryEvent.Request.Url);
        foreach (var (name, value) in sentryEvent.Request.Headers.Where(header => SafeHeaders.Contains(header.Key)))
        {
            clean.Request.Headers[name] = value;
        }

        return clean;
    }

    private static SentryException ScrubException(SentryException original)
    {
        var exception = new SentryException
        {
            Type = original.Type,
            Module = original.Module,
            ThreadId = original.ThreadId,
            Stacktrace = original.Stacktrace,
        };
        if (original.Mechanism is { } mechanism)
        {
            exception.Mechanism = new Mechanism
            {
                Type = mechanism.Type is "generic" or "chained" ? mechanism.Type : "generic",
                Handled = mechanism.Handled,
                Synthetic = mechanism.Synthetic,
                ExceptionId = mechanism.ExceptionId,
                ParentId = mechanism.ParentId,
                Source = mechanism.Source is { } source && MechanismSource().IsMatch(source) ? source : null,
                IsExceptionGroup = mechanism.IsExceptionGroup,
            };
        }

        ScrubFrames(exception.Stacktrace);
        return exception;
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"\AInnerExceptions?(\[[0-9]{1,6}\])?\z")]
    private static partial System.Text.RegularExpressions.Regex MechanismSource();

    private static void ScrubFrames(SentryStackTrace? stackTrace)
    {
        foreach (var frame in stackTrace?.Frames ?? [])
        {
            frame.Vars.Clear();
            frame.ContextLine = null;
            frame.PreContext.Clear();
            frame.PostContext.Clear();
        }
    }

    private static string? PathOnly(string? url) =>
        url is not null && Uri.TryCreate(url, UriKind.Absolute, out var parsed)
            ? parsed.GetComponents(UriComponents.SchemeAndServer | UriComponents.Path, UriFormat.UriEscaped)
            : null;

    public static void ListenForMetrics(this KestrelServerOptions kestrel, System.Net.IPEndPoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(kestrel);
        kestrel.Listen(endpoint, listen => listen.Use(next => connection =>
        {
            connection.Items[MetricsConnection] = true;
            return next(connection);
        }));
    }

    public static void UseAuthMetrics(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.UseOpenTelemetryPrometheusScrapingEndpoint(context => IsMetricsConnection(context) && context.Request.Path == MetricsPath);
        app.Use((HttpContext context, RequestDelegate next) =>
        {
            if (!IsMetricsConnection(context))
            {
                return next(context);
            }

            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return Task.CompletedTask;
        });
    }

    private static bool IsMetricsConnection(HttpContext context) =>
        context.Features.Get<IConnectionItemsFeature>()?.Items.ContainsKey(MetricsConnection) == true;
}

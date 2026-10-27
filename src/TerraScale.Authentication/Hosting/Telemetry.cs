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
using TerraScale.Authentication.Store;

namespace TerraScale.Authentication.Hosting;

/// <summary>
/// Prometheus metrics on the metrics listener (always), OpenTelemetry traces and logs pushed over OTLP when configured,
/// plus error reporting to GlitchTip through the Sentry SDK when configured.
/// </summary>
public static partial class Telemetry
{
    /// <summary>Service name reported in telemetry resources.</summary>
    public const string ServiceName = "ts-auth";

    /// <summary>The Prometheus scrape path, served only on <see cref="AuthOptions.MetricsListen"/>.</summary>
    public const string MetricsPath = "/metrics";

    private static readonly object MetricsConnection = new();

    // Header values are client-chosen text (User-Agent and Accept included), so only the
    // numeric body length reaches GlitchTip.
    private static readonly HashSet<string> SafeHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Content-Length",
    };

    private static bool OtlpConfigured(string signal) =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT"))
        || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable($"OTEL_EXPORTER_OTLP_{signal}_ENDPOINT"));

    /// <summary>Registers telemetry for the configured exporters.</summary>
    public static WebApplicationBuilder AddAuthTelemetry(this WebApplicationBuilder builder, AuthOptions options)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(options);

        // First: it clears the default providers, so the providers added below are kept. Framework
        // categories stay at Warning because their Information lines log query strings (codes, tokens,
        // states). Suppress protocol chatter to preserve the production log window; hosting lifecycle
        // messages remain at Information through the shared logger.
        builder.Logging.AddTerraScaleLogging("ts-auth", ("OpenIddict", LogLevel.Warning), ("Microsoft", LogLevel.Warning));

        // Every provider sees exceptions only as type names and stack traces.
        builder.Services.AddSafeLogging();

        // Standard OTEL_* variables select the collector. Traces and logs export only when the
        // shared endpoint or their own endpoint is set; metrics are always scraped (/metrics).
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
                .AddMeter(AuthMetrics.MeterName, AuthStorageTelemetry.SourceName)
                .AddPrometheusExporter());
        if (traces)
        {
            telemetry.WithTracing(tracing => tracing
                .AddSource(AuthStorageTelemetry.SourceName)
                .AddAspNetCoreInstrumentation(aspnet =>
                    aspnet.Filter = context => !context.Request.Path.StartsWithSegments("/healthz", StringComparison.Ordinal))
                .AddHttpClientInstrumentation()
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
                // Errors only: traces go to OpenTelemetry. Events are captured explicitly by
                // ErrorEnvelopeMiddleware (with the original exception, scrubbed below);
                // log entries only become breadcrumbs.
                sentry.TracesSampleRate = 0;
                sentry.MinimumEventLevel = LogLevel.None;
                // Breadcrumbs are free text from every log category; none are recorded.
                sentry.MaxBreadcrumbs = 0;
                sentry.SetBeforeSend((sentryEvent, _) => Scrub(sentryEvent));
            });
        }

        return builder;
    }


    /// <summary>
    /// Reduces an error event to an allowlist before it leaves the process: exception types and
    /// stack frames, plus the request method and URL path the SDK read from the request. Every
    /// field that scope or application code can assign (messages, params, extras, tags,
    /// contexts, transaction, fingerprint, user) is removed, along with cookies, query strings,
    /// bodies, header values and server data.
    /// </summary>
    public static SentryEvent Scrub(SentryEvent sentryEvent)
    {
        ArgumentNullException.ThrowIfNull(sentryEvent);

        // A brand-new event carries only allowlisted data, so nothing attached to the original
        // by scope or application code (breadcrumbs, extras and their keys, tags, contexts,
        // transaction, fingerprint, user, messages) can survive into the report.
        // Sentry 6 exposes EventId and Timestamp read-only, so the rebuilt event gets a new id
        // and a timestamp later only by the in-process delay before BeforeSend; ts-auth never
        // uses the capture id. Debug images (local file paths) are not sent: GlitchTip does not
        // symbolicate .NET frames, which already carry their code locations.
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

    // Exception type, module and code locations only; every free-text field is dropped.
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
            // Type and Source are strings an event processor could set; keep only the SDK's own
            // vocabulary: the generic and chained mechanisms, and InnerException(s)[n].
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

    // A frame keeps its code location (function, module, file, line) and loses every
    // captured value: local variables and source context lines.
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

    // scheme://host[:port]/path, without user-info, query or fragment.
    private static string? PathOnly(string? url) =>
        url is not null && Uri.TryCreate(url, UriKind.Absolute, out var parsed)
            ? parsed.GetComponents(UriComponents.SchemeAndServer | UriComponents.Path, UriFormat.UriEscaped)
            : null;

    /// <summary>Listens on <paramref name="endpoint"/> for the metrics listener, whose connections reach only <see cref="MetricsPath"/>.</summary>
    public static void ListenForMetrics(this KestrelServerOptions kestrel, System.Net.IPEndPoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(kestrel);
        kestrel.Listen(endpoint, listen => listen.Use(next => connection =>
        {
            connection.Items[MetricsConnection] = true;
            return next(connection);
        }));
    }

    /// <summary>
    /// Serves the Prometheus scrape on metrics connections and answers 404 to anything else there; on the public listener
    /// <see cref="MetricsPath"/> is not served.
    /// </summary>
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

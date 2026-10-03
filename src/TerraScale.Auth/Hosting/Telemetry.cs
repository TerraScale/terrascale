using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Sentry;
using Sentry.Extensibility;
using TerraScale.Auth.Configuration;

namespace TerraScale.Auth.Hosting;

/// <summary>
/// OpenTelemetry traces, metrics and logs pushed over OTLP, plus error reporting to
/// GlitchTip through the Sentry SDK. Nothing is exported unless it is configured.
/// </summary>
public static class Telemetry
{
    /// <summary>Service name reported in telemetry resources.</summary>
    public const string ServiceName = "ts-auth";

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

        // Every provider sees exceptions only as type names and stack traces.
        builder.Services.AddSafeLogging();

        // These framework categories log full request and outbound URLs, query strings
        // included, at Information. Query strings carry codes, tokens and states, so only
        // their warnings are kept.
        builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Warning);
        builder.Logging.AddFilter("System.Net.Http.HttpClient", LogLevel.Warning);

        // Standard OTEL_* variables select the collector. Each signal exports only when the
        // shared endpoint or its own endpoint is set, so a traces-only collector never also
        // receives (or fails on) logs and metrics.
        var traces = OtlpConfigured("TRACES");
        var metrics = OtlpConfigured("METRICS");
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

        if (traces || metrics)
        {
            var telemetry = builder.Services.AddOpenTelemetry()
                .ConfigureResource(resource => resource.AddService(ServiceName, serviceVersion: ServiceVersion.Value));
            if (traces)
            {
                telemetry.WithTracing(tracing => tracing
                    .AddAspNetCoreInstrumentation(aspnet =>
                        aspnet.Filter = context => !context.Request.Path.StartsWithSegments("/healthz", StringComparison.Ordinal))
                    .AddHttpClientInstrumentation()
                    .AddOtlpExporter());
            }

            if (metrics)
            {
                telemetry.WithMetrics(meters => meters
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddRuntimeInstrumentation()
                    .AddOtlpExporter());
            }
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
        var request = sentryEvent.Request;
        request.Cookies = null;
        request.QueryString = null;
        request.Data = null;
        request.Url = PathOnly(request.Url);
        foreach (var header in request.Headers.Keys.Where(name => !SafeHeaders.Contains(name)).ToList())
        {
            request.Headers.Remove(header);
        }

        request.Env.Clear();
        sentryEvent.User = new SentryUser();
        sentryEvent.ServerName = null;
        sentryEvent.Message = null;
        sentryEvent.Logger = null;
        // Fingerprints are application-chosen text; grouping falls back to the stack trace.
        sentryEvent.SetFingerprint([]);
        foreach (var exception in sentryEvent.SentryExceptions ?? [])
        {
            exception.Value = null;
            if (exception.Mechanism is { } mechanism)
            {
                mechanism.Description = null;
                mechanism.HelpLink = null;
                mechanism.Data.Clear();
                mechanism.Meta.Clear();
            }

            ScrubFrames(exception.Stacktrace);
        }

        foreach (var thread in sentryEvent.SentryThreads ?? [])
        {
            thread.Name = null;
            ScrubFrames(thread.Stacktrace);
        }

        // The event exposes no way to remove an extra; nulling drops every value.
        foreach (var key in sentryEvent.Extra.Keys.ToList())
        {
            sentryEvent.SetExtra(key, null);
        }

        foreach (var key in sentryEvent.Tags.Keys.ToList())
        {
            sentryEvent.UnsetTag(key);
        }

        foreach (var key in sentryEvent.Contexts.Keys.ToList())
        {
            sentryEvent.Contexts.Remove(key);
        }

        sentryEvent.TransactionName = null;

        return sentryEvent;
    }

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
}

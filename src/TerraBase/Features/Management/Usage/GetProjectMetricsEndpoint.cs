using System.Globalization;
using System.Text;
using Microsoft.Extensions.Primitives;
using TerraBase.Errors;
using TerraBase.Features.ApiKeys;
using TerraBase.Features.UserAuth;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Management.Usage;

/// <summary>A project's analytics as a Prometheus scrape target, for customers' own Prometheus.</summary>
public sealed class GetProjectMetricsEndpoint(IUsageAnalytics analytics, TimeProvider time) : EndpointWithoutRequest<IResult>
{
    private const int Minutes = 5;
    private const string ContentType = "text/plain; version=0.0.4; charset=utf-8";
    private static readonly QueryCollection LastHour = new(new Dictionary<string, StringValues>(StringComparer.Ordinal) { ["range"] = "1h" });

    /// <inheritdoc />
    public override void Configure()
    {
        Get(AnalyticsRoute.Project + "/metrics");
        Name("getProjectMetrics");
        Summary("Exports project analytics for Prometheus");
        Description("Prometheus text exposition (version 0.0.4) of every database the token reaches, per database and observing region: request, error and "
            + "operation rates averaged over the last 5 complete UTC minutes, latency percentiles of the newest complete minute with requests, and the newest "
            + "sampled storage of the last hour. Scrape with an oauth2 client_credentials API key holding databases:read. Counters are best-effort observations "
            + "flushed every 10 seconds, not billing. " + ProjectApiRoute.IdentityDescription);
        Tags("Analytics");
        Produces<string>(StatusCodes.Status200OK, "text/plain");
        Produces<ErrorBody>(StatusCodes.Status400BadRequest);
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
        RequireAuthorization(ApiKeyScopes.DatabasesRead);
        Metadata(new ProjectManagementRoute(), ListenerMetadata.Management, new DatabaseRegions.PlacementAware());
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        HttpContext.Response.Headers.CacheControl = "no-store";
        if (HttpContext.Features.Get<ProjectAccess>() is not { } access || access.DatabaseName is not null && access.DatabaseId is null)
        {
            return ErrorResponse.From(new NotFound());
        }

        if (AnalyticsWindow.Parse(LastHour, time.GetUtcNow()) is not AnalyticsWindow window)
        {
            throw new InvalidOperationException("the fixed metrics window is invalid");
        }

        return await analytics.AnalyticsAsync(access, window, null, null, HttpContext.Response.RegisterForDispose, cancellationToken).ConfigureAwait(false) switch
        {
            ProjectAnalytics result => TypedResults.Text(Format(result), ContentType),
            NotFound error => ErrorResponse.From(error),
            Validation error => ErrorResponse.From(error),
            Unavailable error => ErrorResponse.From(error),
        };
    }

    // These labels name the caller's own databases in a response scoped to one project. They are never recorded on the
    // TerraBase operator meter, so the bounded-label rule for the administrative /metrics scrape does not apply here.
    private static string Format(ProjectAnalytics analytics)
    {
        var requests = new StringBuilder();
        var errors = new StringBuilder();
        var operations = new StringBuilder();
        var latency = new StringBuilder();
        var storage = new StringBuilder();
        foreach (var item in analytics.Items)
        {
            var labels = $"database=\"{Escape(item.DatabaseName)}\",database_id=\"{Escape(item.DatabaseId)}\",region=\"{Escape(item.Region)}\"";
            // The newest bucket is the current, partial minute.
            var complete = item.Series.Take(Math.Max(0, item.Series.Count - 1)).ToList();
            var recent = complete.TakeLast(Minutes).ToList();
            Line(requests, "requests_per_second", labels, recent.Sum(point => point.Requests.Values.Sum()) / (Minutes * 60.0));
            Line(errors, "errors_per_second", labels, recent.Sum(point => point.Errors) / (Minutes * 60.0));
            foreach (var kind in recent.SelectMany(point => point.Operations.Keys).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
            {
                Line(operations, "operations_per_second", $"{labels},kind=\"{Escape(kind)}\"",
                    recent.Sum(point => point.Operations.GetValueOrDefault(kind)) / (Minutes * 60.0));
            }

            if (complete.LastOrDefault(point => point.Latency.P50Ms is not null) is { Latency: var observed })
            {
                Line(latency, "latency_milliseconds", labels + ",quantile=\"0.5\"", observed.P50Ms);
                Line(latency, "latency_milliseconds", labels + ",quantile=\"0.95\"", observed.P95Ms);
                Line(latency, "latency_milliseconds", labels + ",quantile=\"0.99\"", observed.P99Ms);
            }

            if (item.Series.LastOrDefault(point => point.StorageBytes is not null)?.StorageBytes is { } bytes)
            {
                Line(storage, "storage_bytes", labels, bytes);
            }
        }

        var body = new StringBuilder();
        Family(body, "requests_per_second", "Client requests per second over the last 5 complete minutes.", requests);
        Family(body, "errors_per_second", "Error responses per second over the last 5 complete minutes.", errors);
        Family(body, "operations_per_second", "Committed document operations per second by kind over the last 5 complete minutes.", operations);
        Family(body, "latency_milliseconds", "Approximate request latency percentiles of the newest complete minute with requests.", latency);
        Family(body, "storage_bytes", "Newest sampled on-disk storage within the last hour.", storage);
        return body.ToString();
    }

    private static void Family(StringBuilder body, string name, string help, StringBuilder samples) =>
        body.Append(CultureInfo.InvariantCulture, $"# HELP terrascale_database_{name} {help}\n# TYPE terrascale_database_{name} gauge\n").Append(samples);

    private static void Line(StringBuilder samples, string name, string labels, double? value)
    {
        if (value is { } number)
        {
            samples.Append(CultureInfo.InvariantCulture, $"terrascale_database_{name}{{{labels}}} {number.ToString("R", CultureInfo.InvariantCulture)}\n");
        }
    }

    // Label values escape backslash, double quote and line feed (Prometheus text format 0.0.4).
    private static string Escape(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("\"", "\\\"", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal);
}

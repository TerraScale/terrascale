using System.Diagnostics.Metrics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using TerraBase.Hosting;
using ZLogger;

namespace TerraBase.Features.Shapes;

/// <summary>
/// Free reads T2 telemetry (docs/terrabase/free-reads-cdn.md): how Cloudflare answered this node's shape CDN host, read
/// from Cloudflare's zone analytics every few minutes, never counted per request. Nothing runs without an R2 bucket.
/// </summary>
internal sealed class ShapeCdnAnalytics(IHttpClientFactory http, string? host, string? token, TimeProvider time, IMeterFactory meters,
    ILogger<ShapeCdnAnalytics> logger) : BackgroundService
{
    internal static readonly Uri GraphQlUri = new("https://api.cloudflare.com/client/v4/graphql");
    internal static readonly TimeSpan Step = TimeSpan.FromMinutes(5);

    // Cloudflare ingests request analytics within minutes; a window is read only once it is this old, so a late row
    // is not lost. Windows are half-open and adjacent, so no request is counted twice.
    internal static readonly TimeSpan Delay = TimeSpan.FromMinutes(10);

    // After a long outage the first window starts at most this far back, inside the dataset's query range.
    private static readonly TimeSpan MaxWindow = TimeSpan.FromHours(6);

    private const string Query = """
        query($zone: String!, $host: String!, $start: Time!, $end: Time!) {
          viewer { zones(filter: { zoneTag: $zone }) {
            httpRequestsAdaptiveGroups(limit: 100, filter: { clientRequestHTTPHost: $host, datetime_geq: $start, datetime_lt: $end }) {
              count dimensions { cacheStatus }
            }
          } }
        }
        """;

    private readonly Counter<long> _requests = meters.Create(Telemetry.MeterName).CreateCounter<long>("terrabase.shape.cdn.requests",
        description: "Requests Cloudflare answered on this node's shape CDN host, by cache status (hit, miss, other), from zone analytics about 10 minutes late.");

    // Created with the service, so the first step counts from startup even if ExecuteAsync starts later.
    private readonly PeriodicTimer _timer = new(Step, time);
    private readonly DateTimeOffset _start = Minute(time.GetUtcNow() - Delay);

    public override void Dispose()
    {
        _timer.Dispose();
        base.Dispose();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (host is null || token is null)
        {
            return;
        }

        var start = _start;
        while (await _timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            var end = Minute(time.GetUtcNow() - Delay);
            if (end - start > MaxWindow)
            {
                start = end - MaxWindow;
            }

            if (end > start && await ReadAsync(start, end, stoppingToken).ConfigureAwait(false))
            {
                start = end;
            }
        }
    }

    // True once the window is counted; false leaves it for the next step.
    private async Task<bool> ReadAsync(DateTimeOffset start, DateTimeOffset end, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, GraphQlUri)
            {
                Content = JsonContent.Create(new ShapeCdnAnalyticsRequest(Query, new(ShapeCdnPurge.Zone, host!, Text(start), Text(end))),
                    ShapeCdnAnalyticsJsonContext.Default.ShapeCdnAnalyticsRequest),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await http.CreateClient(ShapeCdnPurge.HttpClientName).SendAsync(request, cancellationToken).ConfigureAwait(false);
            var body = response.IsSuccessStatusCode
                ? await response.Content.ReadFromJsonAsync(ShapeCdnAnalyticsJsonContext.Default.ShapeCdnAnalyticsResponse, cancellationToken).ConfigureAwait(false)
                : null;
            if (body is not { Errors: null or [], Data.Viewer.Zones: [var zone] })
            {
                logger.ZLogWarning($"Shape CDN analytics answered HTTP {(int)response.StatusCode} without data; the window is read again next time");
                return false;
            }

            foreach (var group in zone.HttpRequestsAdaptiveGroups)
            {
                _requests.Add(group.Count, new KeyValuePair<string, object?>("cache_status", Status(group.Dimensions.CacheStatus)));
            }

            return true;
        }
        catch (Exception error) when (!cancellationToken.IsCancellationRequested && error is HttpRequestException or TaskCanceledException or JsonException)
        {
            logger.ZLogWarning(error, $"Shape CDN analytics request failed; the window is read again next time");
            return false;
        }
    }

    // Bounded labels: served from the cache, read from the bucket, or neither (blocked, errors).
    private static string Status(string? status) => status switch
    {
        "hit" or "stale" or "updating" or "revalidated" => "hit",
        "miss" or "expired" => "miss",
        _ => "other",
    };

    private static DateTimeOffset Minute(DateTimeOffset at) => new(at.Year, at.Month, at.Day, at.Hour, at.Minute, 0, TimeSpan.Zero);

    private static string Text(DateTimeOffset at) => at.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
}

internal sealed record ShapeCdnAnalyticsRequest(string Query, ShapeCdnAnalyticsVariables Variables);

internal sealed record ShapeCdnAnalyticsVariables(string Zone, string Host, string Start, string End);

internal sealed record ShapeCdnAnalyticsResponse(ShapeCdnAnalyticsData? Data, IReadOnlyList<JsonElement>? Errors);

internal sealed record ShapeCdnAnalyticsData(ShapeCdnAnalyticsViewer Viewer);

internal sealed record ShapeCdnAnalyticsViewer(IReadOnlyList<ShapeCdnAnalyticsZone> Zones);

internal sealed record ShapeCdnAnalyticsZone(IReadOnlyList<ShapeCdnAnalyticsGroup> HttpRequestsAdaptiveGroups);

internal sealed record ShapeCdnAnalyticsGroup(long Count, ShapeCdnAnalyticsDimensions Dimensions);

internal sealed record ShapeCdnAnalyticsDimensions(string? CacheStatus);

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(ShapeCdnAnalyticsRequest))]
[JsonSerializable(typeof(ShapeCdnAnalyticsResponse))]
internal sealed partial class ShapeCdnAnalyticsJsonContext : JsonSerializerContext;

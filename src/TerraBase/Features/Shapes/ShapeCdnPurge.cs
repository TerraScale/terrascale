using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using TerraBase.Hosting;
using ZLogger;

namespace TerraBase.Features.Shapes;

/// <summary>
/// Free reads T2 cleanup (docs/terrabase/free-reads-cdn.md): once the mirror deletes an R2 object, its copies leave
/// Cloudflare's cache too, by single-file purge on <c>terrascale.tech</c>.
/// </summary>
internal interface IShapeCdnPurge
{
    /// <summary>
    /// Queues the CDN URL of a deleted object. False when the bounded queue is full: the copy is TBS1 ciphertext whose
    /// object no manifest names any more, and it leaves the cache when Cloudflare evicts it.
    /// </summary>
    bool Queue(string url);
}

/// <summary>Batches queued URLs into Cloudflare purge requests, retrying throttled and failed ones a bounded number of times.</summary>
internal sealed class ShapeCdnPurge : BackgroundService, IShapeCdnPurge
{
    internal const string HttpClientName = "cloudflare-api";

    /// <summary>The <c>terrascale.tech</c> zone, which serves every <c>shapes-&lt;bucket&gt;</c> host (<see cref="ShapeObjectStore.Cdn"/>).</summary>
    internal static readonly Uri PurgeUri = new("https://api.cloudflare.com/client/v4/zones/4cc466e19478c116fae7dc445a6d0911/purge_cache");

    // A URL is under 200 bytes (fixed host, handle and SHA-256 name), so 4096 of them also bound the queue to under 1 MiB.
    private const int QueueItems = 4096;
    // Cloudflare's single-file purge takes at most 100 URLs per request on every plan.
    private const int BatchUrls = 100;
    private const int Attempts = 5;
    private static readonly TimeSpan FirstRetry = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaxRetry = TimeSpan.FromMinutes(1);
    private readonly Channel<string> _queue = Channel.CreateBounded<string>(new BoundedChannelOptions(QueueItems) { SingleReader = true });
    private readonly IHttpClientFactory _http;
    private readonly string? _token;
    private readonly TimeProvider _time;
    private readonly ILogger<ShapeCdnPurge> _logger;
    private readonly Counter<long> _purges;

    public ShapeCdnPurge(IHttpClientFactory http, string? token, TimeProvider time, IMeterFactory meters, ILogger<ShapeCdnPurge> logger)
    {
        _http = http;
        _token = token;
        _time = time;
        _logger = logger;
        _purges = meters.Create(Telemetry.MeterName).CreateCounter<long>("terrabase.shape.cdn.purges",
            description: "Shape CDN URLs by purge outcome: purged, failed (Cloudflare refused or kept failing) or dropped (queue full).");
    }

    public bool Queue(string url)
    {
        if (_queue.Writer.TryWrite(url))
        {
            return true;
        }

        _purges.Add(1, new KeyValuePair<string, object?>("outcome", "dropped"));
        return false;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var batch = new List<string>(BatchUrls);
        while (await _queue.Reader.WaitToReadAsync(stoppingToken).ConfigureAwait(false))
        {
            batch.Clear();
            while (batch.Count < BatchUrls && _queue.Reader.TryRead(out var url))
            {
                batch.Add(url);
            }

            var outcome = await PurgeAsync(batch, stoppingToken).ConfigureAwait(false) ? "purged" : "failed";
            _purges.Add(batch.Count, new KeyValuePair<string, object?>("outcome", outcome));
        }
    }

    private async Task<bool> PurgeAsync(List<string> urls, CancellationToken cancellationToken)
    {
        var client = _http.CreateClient(HttpClientName);
        var wait = FirstRetry;
        for (var attempt = 1; ; attempt++)
        {
            HttpStatusCode? status = null;
            TimeSpan? retryAfter = null;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, PurgeUri)
                {
                    Content = JsonContent.Create(new ShapeCdnPurgeRequest(urls), ShapeCdnPurgeJsonContext.Default.ShapeCdnPurgeRequest),
                };
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
                using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
                status = response.StatusCode;
                retryAfter = response.Headers.RetryAfter?.Delta;
                if (response.IsSuccessStatusCode
                    && await response.Content.ReadFromJsonAsync(ShapeCdnPurgeJsonContext.Default.ShapeCdnPurgeResponse, cancellationToken).ConfigureAwait(false) is { Success: true })
                {
                    return true;
                }
            }
            catch (Exception error) when (!cancellationToken.IsCancellationRequested && error is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
            {
                _logger.ZLogWarning(error, $"Shape CDN purge request failed");
            }

            // A refusal other than throttling (bad token, unknown zone) does not improve by retrying.
            if (attempt == Attempts || status is { } code && (int)code is >= 400 and < 500 && code != HttpStatusCode.TooManyRequests)
            {
                _logger.ZLogWarning($"Shape CDN purge gave up on {urls.Count} URLs after {attempt} attempts (last status {(status is null ? 0 : (int)status)})");
                return false;
            }

            await Task.Delay(retryAfter is { } after && after > wait ? after : wait, _time, cancellationToken).ConfigureAwait(false);
            wait = TimeSpan.FromTicks(Math.Min(wait.Ticks * 2, MaxRetry.Ticks));
        }
    }
}

internal sealed record ShapeCdnPurgeRequest(IReadOnlyList<string> Files);

internal sealed record ShapeCdnPurgeResponse(bool Success);

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(ShapeCdnPurgeRequest))]
[JsonSerializable(typeof(ShapeCdnPurgeResponse))]
internal sealed partial class ShapeCdnPurgeJsonContext : JsonSerializerContext;

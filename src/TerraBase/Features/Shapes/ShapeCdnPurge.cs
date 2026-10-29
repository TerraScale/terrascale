using System.Buffers;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using TerraBase.Configuration;
using TerraBase.Hosting;
using ZLogger;

namespace TerraBase.Features.Shapes;

internal interface IShapeCdnPurge
{
    bool Queue(string url);
}

internal sealed class ShapeCdnPurge(IHttpClientFactory http, string? token, TimeProvider time, IMeterFactory meters, ILogger<ShapeCdnPurge> logger)
    : BackgroundService, IShapeCdnPurge
{
    internal const string HttpClientName = "cloudflare-api";

    internal const string Zone = "4cc466e19478c116fae7dc445a6d0911";

    internal static readonly Uri PurgeUri = new($"https://api.cloudflare.com/client/v4/zones/{Zone}/purge_cache");

    private const int QueueItems = 4096;
    private const int BatchUrls = 100;
    private const int Attempts = 5;
    private const string ObjectPrefix = "https://" + ShapeStorageSettings.CdnHost + "/v1/shapes/";
    private static readonly SearchValues<char> LowerHex = SearchValues.Create("0123456789abcdef");
    private static readonly TimeSpan RequestInterval = TimeSpan.FromSeconds(12);
    private DateTimeOffset _nextRequest;
    private static readonly TimeSpan FirstRetry = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaxRetry = TimeSpan.FromMinutes(1);
    private readonly Channel<string> _queue = Channel.CreateBounded<string>(new BoundedChannelOptions(QueueItems) { SingleReader = true });
    private readonly Counter<long> _purges = meters.Create(Telemetry.MeterName).CreateCounter<long>("terrabase.shape.cdn.purges",
        description: "Shape CDN handle prefixes by purge outcome: purged, failed (Cloudflare refused or kept failing) or dropped (queue full).");

    public bool Queue(string url)
    {
        if (url.Length != ObjectPrefix.Length + 84 || !url.StartsWith(ObjectPrefix, StringComparison.Ordinal)
            || !url.AsSpan(ObjectPrefix.Length, 3).SequenceEqual("h1-") || url[ObjectPrefix.Length + 19] != '/'
            || url.AsSpan(ObjectPrefix.Length + 3, 16).ContainsAnyExcept(LowerHex)
            || url.AsSpan(ObjectPrefix.Length + 20).ContainsAnyExcept(LowerHex))
        {
            return false;
        }

        var prefix = url[8..(ObjectPrefix.Length + 20)];
        if (_queue.Writer.TryWrite(prefix))
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
            for (var scanned = 0; scanned < BatchUrls && _queue.Reader.TryRead(out var url); scanned++)
            {
                if (!batch.Contains(url, StringComparer.Ordinal))
                {
                    batch.Add(url);
                }
            }

            var outcome = await PurgeAsync(batch, stoppingToken).ConfigureAwait(false) ? "purged" : "failed";
            _purges.Add(batch.Count, new KeyValuePair<string, object?>("outcome", outcome));
        }
    }

    private async Task<bool> PurgeAsync(List<string> urls, CancellationToken cancellationToken)
    {
        var client = http.CreateClient(HttpClientName);
        var wait = FirstRetry;
        for (var attempt = 1; ; attempt++)
        {
            var delay = _nextRequest - time.GetUtcNow();
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, time, cancellationToken).ConfigureAwait(false);
            }
            _nextRequest = time.GetUtcNow() + RequestInterval;
            HttpStatusCode? status = null;
            TimeSpan? retryAfter = null;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, PurgeUri)
                {
                    Content = JsonContent.Create(new ShapeCdnPurgeRequest(urls), ShapeCdnPurgeJsonContext.Default.ShapeCdnPurgeRequest),
                };
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
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
                logger.ZLogWarning(error, $"Shape CDN purge request failed");
            }

            if (attempt == Attempts || status is { } code && (int)code is >= 400 and < 500 && code != HttpStatusCode.TooManyRequests)
            {
                logger.ZLogWarning($"Shape CDN purge gave up on {urls.Count} URLs after {attempt} attempts (last status {(status is null ? 0 : (int)status)})");
                return false;
            }

            await Task.Delay(retryAfter is { } after && after > wait ? after : wait, time, cancellationToken).ConfigureAwait(false);
            wait = TimeSpan.FromTicks(Math.Min(wait.Ticks * 2, MaxRetry.Ticks));
        }
    }
}

internal sealed record ShapeCdnPurgeRequest(IReadOnlyList<string> Prefixes);

internal sealed record ShapeCdnPurgeResponse(bool Success);

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(ShapeCdnPurgeRequest))]
[JsonSerializable(typeof(ShapeCdnPurgeResponse))]
internal sealed partial class ShapeCdnPurgeJsonContext : JsonSerializerContext;

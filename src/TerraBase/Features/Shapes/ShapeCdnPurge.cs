using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Security.Cryptography;
using System.Text;
using TerraBase.Features.Backup;
using TerraBase.Features.Management;
using TerraBase.Configuration;
using TerraBase.Hosting;
using ZLogger;

namespace TerraBase.Features.Shapes;

internal interface IShapeCdnPurge
{
    Task<bool> QueueAsync(string url, CancellationToken cancellationToken);
}

internal sealed class ShapeCdnPurge(IHttpClientFactory http, string? token, TimeProvider time, IMeterFactory meters, ILogger<ShapeCdnPurge> logger, SystemCatalog catalog, KeyCustody? custody)
    : BackgroundService, IShapeCdnPurge
{
    internal const string HttpClientName = "cloudflare-api";
    internal const string VerificationClientName = "shape-cdn-verification";

    internal const string Zone = "4cc466e19478c116fae7dc445a6d0911";

    internal static readonly Uri PurgeUri = new($"https://api.cloudflare.com/client/v4/zones/{Zone}/purge_cache");

    private const int QueueItems = 2000;
    private const int BatchUrls = 100;
    private const int Attempts = 5;
    private const string ObjectPrefix = "https://" + ShapeStorageSettings.CdnHost + "/v1/shapes/";
    private static readonly SearchValues<char> LowerHex = SearchValues.Create("0123456789abcdef");
    private static readonly TimeSpan RequestInterval = TimeSpan.FromSeconds(12);
    private DateTimeOffset _nextRequest;
    private static readonly TimeSpan FirstRetry = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaxRetry = TimeSpan.FromMinutes(1);
    private const string Outbox = "shape_cdn_purges";
    private long _pending;
    private long _oldestTicks;
    private readonly Counter<long> _attempts = meters.Create(Telemetry.MeterName).CreateCounter<long>("terrabase.shape.cdn.purge_attempts",
        description: "Cloudflare purge requests by accepted or failed result; includes retries.");
    private readonly Counter<long> _purges = meters.Create(Telemetry.MeterName).CreateCounter<long>("terrabase.shape.cdn.purges",
        description: "Shape CDN handle prefixes by purge outcome: purged, failed (Cloudflare refused or kept failing) or dropped (queue full).");

    public async Task<bool> QueueAsync(string url, CancellationToken cancellationToken)
    {
        if (url.Length != ObjectPrefix.Length + 84 || !url.StartsWith(ObjectPrefix, StringComparison.Ordinal)
            || !url.AsSpan(ObjectPrefix.Length, 3).SequenceEqual("h1-") || url[ObjectPrefix.Length + 19] != '/'
            || url.AsSpan(ObjectPrefix.Length + 3, 16).ContainsAnyExcept(LowerHex)
            || url.AsSpan(ObjectPrefix.Length + 20).ContainsAnyExcept(LowerHex))
        {
            return false;
        }
        var id = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(url)));
        return await catalog.WriteUncheckedAsync(async transaction =>
        {
            if (await transaction.GetAsync(Outbox, id, cancellationToken).ConfigureAwait(false) is not null)
            {
                return true;
            }
            var page = await transaction.ListAsync(Outbox, null, QueueItems, QueueItems * 1024L, cancellationToken).ConfigureAwait(false);
            if (page.Records.Count == QueueItems || page.HasMore)
            {
                _purges.Add(1, new KeyValuePair<string, object?>("outcome", "dropped"));
                return false;
            }
            _ = await transaction.PutAsync(Outbox, id, JsonSerializer.SerializeToUtf8Bytes(new ShapeCdnPurgeWork(url, time.GetUtcNow()),
                ShapeCdnPurgeJsonContext.Default.ShapeCdnPurgeWork), cancellationToken).ConfigureAwait(false);
            return true;
        }, cancellationToken, durable: true).ConfigureAwait(false);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var meter = meters.Create(Telemetry.MeterName);
        meter.CreateObservableGauge("terrabase.shape.cdn.purge_pending", () => Interlocked.Read(ref _pending),
            description: "Durable CDN purge URLs awaiting verified removal.");
        meter.CreateObservableGauge("terrabase.shape.cdn.purge_oldest_age", () =>
            Interlocked.Read(ref _oldestTicks) is > 0 and var ticks ? Math.Max(0, (time.GetUtcNow() - new DateTimeOffset(ticks, TimeSpan.Zero)).TotalSeconds) : 0,
            "s", "Age of the oldest queued CDN URL; healthy-provider removal bound is 420 seconds.");
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2), time);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                await RunOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception) when (!stoppingToken.IsCancellationRequested)
            {
                _purges.Add(1, new KeyValuePair<string, object?>("outcome", "unavailable"));
                logger.ZLogWarning($"Shape CDN purge outbox is unavailable; durable work retries");
            }
        }
    }

    internal async Task RunOnceAsync(CancellationToken token)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(18), time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token);
        token = linked.Token;
        var inventory = await catalog.ReadUncheckedAsync(transaction => transaction.ListAsync(Outbox, null, QueueItems, QueueItems * 1024L, token), token).ConfigureAwait(false);
        var work = inventory.Records.Select(record => (record.Id, Work: ReadWork(record.Json.Span))).ToArray();
        Interlocked.Exchange(ref _pending, work.Length);
        Interlocked.Exchange(ref _oldestTicks, work.Length == 0 ? 0 : work.Min(item => item.Work.QueuedAt.UtcTicks));
        var page = work.OrderBy(item => item.Work.QueuedAt).ThenBy(item => item.Id, StringComparer.Ordinal).Take(BatchUrls).ToArray();
        if (page.Length == 0)
        {
            return;
        }
        var ready = new ConcurrentBag<(string Id, string Url, string Prefix)>();
        if (custody is null)
        {
            return;
        }
        await Parallel.ForEachAsync(page, new ParallelOptions { MaxDegreeOfParallelism = 32, CancellationToken = token }, async (record, cancellationToken) =>
        {
            var url = record.Work.Url;
            if (!await custody.Store.ReadAsync(new Uri(url).AbsolutePath[1..], static (_, _) => Task.CompletedTask, cancellationToken).ConfigureAwait(false))
            {
                ready.Add((record.Id, url, url[8..(ObjectPrefix.Length + 20)]));
            }
        }).ConfigureAwait(false);
        if (!ready.IsEmpty)
        {
            var batch = ready.Select(item => item.Prefix).Distinct(StringComparer.Ordinal).ToList();
            var success = await PurgeAsync(batch, token).ConfigureAwait(false);
            if (success)
            {
                var client = http.CreateClient(VerificationClientName);
                var unverified = 0;
                await Parallel.ForEachAsync(ready, new ParallelOptions { MaxDegreeOfParallelism = 32, CancellationToken = token }, async (item, cancellationToken) =>
                {
                    using var response = await client.GetAsync(item.Url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                    if (response.StatusCode != HttpStatusCode.NotFound)
                    {
                        Interlocked.Increment(ref unverified);
                    }
                }).ConfigureAwait(false);
                success = unverified == 0;
                _purges.Add(unverified, new KeyValuePair<string, object?>("outcome", "verification_failed"));
            }
            _purges.Add(batch.Count, new KeyValuePair<string, object?>("outcome", success ? "purged" : "failed"));
            if (success)
            {
                _ = await catalog.WriteUncheckedAsync(async transaction =>
                {
                    foreach (var item in ready)
                    {
                        _ = await transaction.DeleteAsync(Outbox, item.Id, token).ConfigureAwait(false);
                    }
                    return true;
                }, token, durable: true).ConfigureAwait(false);
                Interlocked.Add(ref _pending, -ready.Count);
            }
        }
    }

    private ShapeCdnPurgeWork ReadWork(ReadOnlySpan<byte> json) => json[0] == (byte)'"'
        ? new(JsonSerializer.Deserialize(json, ShapeCdnPurgeJsonContext.Default.String)!, time.GetUtcNow())
        : JsonSerializer.Deserialize(json, ShapeCdnPurgeJsonContext.Default.ShapeCdnPurgeWork)!;

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
                    _attempts.Add(1, new KeyValuePair<string, object?>("outcome", "accepted"));
                    return true;
                }
            }

            catch (Exception error) when (!cancellationToken.IsCancellationRequested && error is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
            {
                logger.ZLogWarning(error, $"Shape CDN purge request failed");
            }
            _attempts.Add(1, new KeyValuePair<string, object?>("outcome", "failed"));

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

internal sealed record ShapeCdnPurgeWork(string Url, DateTimeOffset QueuedAt);

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(ShapeCdnPurgeRequest))]
[JsonSerializable(typeof(ShapeCdnPurgeResponse))]
[JsonSerializable(typeof(ShapeCdnPurgeWork))]
internal sealed partial class ShapeCdnPurgeJsonContext : JsonSerializerContext;

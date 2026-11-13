using System.Buffers.Text;
using System.Globalization;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TerraBase.Configuration;
using TerraBase.Engine;
using TerraBase.Features.Management;
using TerraBase.Hosting;
using TerraBase.Tenancy;
using ZLogger;

namespace TerraBase.Features.Atlas;

internal interface IAtlasPlacements
{
    Task QueueAsync(string project, string database, string? region, CancellationToken cancellationToken);
}

internal sealed class NoAtlasPlacements : IAtlasPlacements
{
    public static readonly NoAtlasPlacements Instance = new();

    public Task QueueAsync(string project, string database, string? region, CancellationToken cancellationToken) => Task.CompletedTask;
}

internal sealed class AtlasPlacementPublisher(
    IHttpClientFactory http, ECDsa key, Uri atlasUrl, TimeProvider time, IMeterFactory meters, ILogger<AtlasPlacementPublisher> logger, SystemCatalog catalog, TerraBaseOptions options)
    : BackgroundService, IAtlasPlacements
{
    internal const string HttpClientName = "atlas-publish";

    private const string Outbox = "atlas_placement_outbox";
    private const string Published = "atlas_placement_published";
    private const int Batch = 64;
    private const int Listed = 1024;
    private static readonly TimeSpan FirstRetry = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaxRetry = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ResyncInterval = TimeSpan.FromMinutes(10);
    private long _pending;
    private readonly Counter<long> _publishes = meters.Create(Telemetry.MeterName).CreateCounter<long>("terrabase.atlas.placement_publishes",
        description: "Database placements POSTed to Atlas by result: updated, stale, retryable, refused or queue_failed.");

    public async Task QueueAsync(string project, string database, string? region, CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10), time);
            var queued = await catalog.WriteUncheckedAsync(async transaction =>
            {
                await EnqueueAsync(transaction, project, database, region, timeout.Token).ConfigureAwait(false);
                return true;
            }, static _ => false, timeout.Token, durable: true).ConfigureAwait(false);
            if (!queued)
            {
                throw new InvalidOperationException("the system catalog is unavailable for the Atlas placement queue");
            }
        }
        catch (Exception error)
        {
            logger.ZLogError(error, $"Queueing Atlas placement for project {project:@Project} and database {database:@Database} failed");
            _publishes.Add(1, new KeyValuePair<string, object?>("result", "queue_failed"));
        }
    }

    private async ValueTask EnqueueAsync(IStorageTransaction transaction, string project, string database, string? region, CancellationToken cancellationToken)
    {
        var id = Key(project, database);
        var previous = await transaction.GetAsync(Outbox, id, cancellationToken).ConfigureAwait(false) is { } stored
            ? JsonSerializer.Deserialize(stored.Json.Span, AtlasPlacementsJsonContext.Default.AtlasPlacementWork)
            : null;
        var published = await transaction.GetAsync(Published, id, cancellationToken).ConfigureAwait(false) is { } last
            ? JsonSerializer.Deserialize(last.Json.Span, AtlasPlacementsJsonContext.Default.AtlasPlacementStamp)?.Updated ?? 0
            : 0;
        var now = time.GetUtcNow().ToUnixTimeSeconds();
        var updated = Math.Max(now, Math.Max(published + 1, (previous?.Updated ?? 0) + 1));
        _ = await transaction.PutAsync(Outbox, id, JsonSerializer.SerializeToUtf8Bytes(
            new AtlasPlacementWork(project, database, region, updated, 0, now), AtlasPlacementsJsonContext.Default.AtlasPlacementWork), cancellationToken).ConfigureAwait(false);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        meters.Create(Telemetry.MeterName).CreateObservableGauge("terrabase.atlas.placement_pending", () => Volatile.Read(ref _pending),
            description: "Database placements waiting in the Atlas outbox.");
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2), time);
        var nextResync = DateTimeOffset.MinValue;
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                var now = time.GetUtcNow();
                if (now >= nextResync)
                {
                    nextResync = now + ResyncInterval;
                    await ResyncAsync(stoppingToken).ConfigureAwait(false);
                }

                await RunOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception) when (!stoppingToken.IsCancellationRequested)
            {
                logger.ZLogWarning($"Atlas placement publishing is unavailable; durable outbox work retries");
            }
        }
    }

    private async Task ResyncAsync(CancellationToken cancellationToken)
    {
        TenantContext? after = null;
        while (true)
        {
            var page = await catalog.ReadUncheckedAsync(
                async transaction => await CatalogTables.DatabasesHomedAsync(transaction, options.Region, options.Region, after, Batch, cancellationToken).ConfigureAwait(false),
                cancellationToken).ConfigureAwait(false);
            foreach (var database in page)
            {
                var queued = await catalog.WriteUncheckedAsync(async transaction =>
                {
                    var current = await CatalogTables.DatabaseAsync(transaction, database.ProjectId!, database.DatabaseId!, cancellationToken).ConfigureAwait(false);
                    if (current is not null && current.Home(options.Region) == options.Region)
                    {
                        await EnqueueAsync(transaction, current.ProjectId, current.Name, current.Home(options.Region), cancellationToken).ConfigureAwait(false);
                    }

                    return true;
                }, static _ => false, cancellationToken, durable: true).ConfigureAwait(false);
                if (!queued)
                {
                    throw new InvalidOperationException("the system catalog is unavailable for the Atlas placement resync");
                }
            }

            if (page.Count < Batch)
            {
                return;
            }

            after = page[^1];
        }
    }

    internal async Task RunOnceAsync(CancellationToken token)
    {
        var now = time.GetUtcNow();
        var inventory = await catalog.ReadUncheckedAsync(async transaction =>
        {
            string? after = null;
            long pending = 0;
            (string Id, AtlasPlacementWork Work)[] due = [];
            RecordPage page;
            do
            {
                page = await transaction.ListAsync(Outbox, after, Listed, Listed * 2048L, token).ConfigureAwait(false);
                pending += page.Records.Count;
                due = [.. due.Concat(page.Records.Select(record =>
                        (Id: record.Id, Work: JsonSerializer.Deserialize(record.Json.Span, AtlasPlacementsJsonContext.Default.AtlasPlacementWork)!)))
                    .Where(item => item.Work.NextAt <= now.ToUnixTimeSeconds())
                    .OrderBy(item => item.Work.NextAt).ThenBy(item => item.Id, StringComparer.Ordinal)
                    .Take(Batch)];
                after = page.Records.Count > 0 ? page.Records[^1].Id : after;
            }
            while (page.HasMore);

            return (Pending: pending, Rows: due);
        }, token).ConfigureAwait(false);
        Interlocked.Exchange(ref _pending, inventory.Pending);
        var rows = inventory.Rows;
        if (rows.Length == 0)
        {
            return;
        }

        var body = JsonSerializer.SerializeToUtf8Bytes(
            new AtlasPublishRequest([.. rows.Select(item => new AtlasPlacement(1, item.Work.Project, item.Work.Database, item.Work.Region, item.Work.Updated))]),
            AtlasPlacementsJsonContext.Default.AtlasPublishRequest);
        var answer = await PublishAsync(body, token).ConfigureAwait(false);
        if (answer is AtlasRefusal refusal)
        {
            logger.ZLogError($"Atlas refused placement publish with status {refusal.Status}; dropping {rows.Length} placements that can never land");
            await RemoveAsync(rows, published: false, token).ConfigureAwait(false);
            _publishes.Add(rows.Length, new KeyValuePair<string, object?>("result", "refused"));
            return;
        }

        if (answer is not AtlasAcceptance accepted)
        {
            _publishes.Add(rows.Length, new KeyValuePair<string, object?>("result", "retryable"));
            await RetryAsync(rows, token).ConfigureAwait(false);
            return;
        }

        var settled = new List<(string Id, AtlasPlacementWork Work)>();
        var retryable = new List<(string Id, AtlasPlacementWork Work)>();
        foreach (var item in rows)
        {
            if (accepted.Results.TryGetValue((item.Work.Project, item.Work.Database), out var outcome) && outcome is "updated" or "stale")
            {
                _publishes.Add(1, new KeyValuePair<string, object?>("result", outcome));
                settled.Add(item);
            }
            else
            {
                retryable.Add(item);
            }
        }

        if (settled.Count > 0)
        {
            await RemoveAsync([.. settled], published: true, token).ConfigureAwait(false);
        }

        if (retryable.Count > 0)
        {
            _publishes.Add(retryable.Count, new KeyValuePair<string, object?>("result", "retryable"));
            await RetryAsync([.. retryable], token).ConfigureAwait(false);
        }
    }

    private async Task<AtlasOutcome> PublishAsync(byte[] body, CancellationToken token)
    {
        var stamp = time.GetUtcNow().ToUnixTimeSeconds();
        var prefix = Encoding.UTF8.GetBytes("atlas-publish-v1\n" + stamp.ToString(CultureInfo.InvariantCulture) + "\n");
        var payload = new byte[prefix.Length + body.Length];
        prefix.CopyTo(payload, 0);
        body.CopyTo(payload, prefix.Length);
        var signature = key.SignData(payload, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(atlasUrl, "v1/publish"))
        {
            Content = new ByteArrayContent(body),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Headers.Add("X-Atlas-Timestamp", stamp.ToString(CultureInfo.InvariantCulture));
        request.Headers.Add("X-Atlas-Signature", Base64Url.EncodeToString(signature));
        HttpStatusCode? status = null;
        try
        {
            var client = http.CreateClient(HttpClientName);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            status = response.StatusCode;
            if (response.IsSuccessStatusCode
                && await response.Content.ReadFromJsonAsync(AtlasPlacementsJsonContext.Default.AtlasPublishResponse, token).ConfigureAwait(false) is { } parsed)
            {
                return new AtlasAcceptance(parsed.Placements.ToDictionary(item => (item.Project, item.Database), item => item.Result));
            }
        }
        catch (Exception error) when (!token.IsCancellationRequested && error is HttpRequestException or TaskCanceledException or JsonException)
        {
            logger.ZLogWarning(error, $"Atlas placement publish request failed");
        }

        return status is { } code && (int)code is >= 400 and < 500 && code is not (HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests)
            ? new AtlasRefusal((int)code)
            : new AtlasRetry();
    }

    private async Task RemoveAsync((string Id, AtlasPlacementWork Work)[] rows, bool published, CancellationToken token)
    {
        _ = await catalog.WriteUncheckedAsync(async transaction =>
        {
            foreach (var item in rows)
            {
                var unchanged = Unchanged(await transaction.GetAsync(Outbox, item.Id, token).ConfigureAwait(false), item.Work.Updated);
                if (published)
                {
                    if (item.Work.Region is null && unchanged)
                    {
                        _ = await transaction.DeleteAsync(Published, item.Id, token).ConfigureAwait(false);
                    }
                    else
                    {
                        var previous = await transaction.GetAsync(Published, item.Id, token).ConfigureAwait(false) is { } stored
                            ? JsonSerializer.Deserialize(stored.Json.Span, AtlasPlacementsJsonContext.Default.AtlasPlacementStamp)?.Updated ?? 0
                            : 0;
                        _ = await transaction.PutAsync(Published, item.Id, JsonSerializer.SerializeToUtf8Bytes(
                            new AtlasPlacementStamp(Math.Max(previous, item.Work.Updated)), AtlasPlacementsJsonContext.Default.AtlasPlacementStamp), token).ConfigureAwait(false);
                    }
                }

                if (unchanged)
                {
                    _ = await transaction.DeleteAsync(Outbox, item.Id, token).ConfigureAwait(false);
                }
            }

            return true;
        }, static _ => false, token, durable: true).ConfigureAwait(false);
    }

    private async Task RetryAsync((string Id, AtlasPlacementWork Work)[] rows, CancellationToken token)
    {
        var now = time.GetUtcNow().ToUnixTimeSeconds();
        _ = await catalog.WriteUncheckedAsync(async transaction =>
        {
            foreach (var item in rows)
            {
                if (await transaction.GetAsync(Outbox, item.Id, token).ConfigureAwait(false) is not { } stored
                    || JsonSerializer.Deserialize(stored.Json.Span, AtlasPlacementsJsonContext.Default.AtlasPlacementWork) is not { } current
                    || current.Updated != item.Work.Updated)
                {
                    continue;
                }

                var delay = TimeSpan.FromTicks(Math.Min((FirstRetry * (1L << Math.Min(current.Attempts, 6))).Ticks, MaxRetry.Ticks));
                _ = await transaction.PutAsync(Outbox, item.Id, JsonSerializer.SerializeToUtf8Bytes(
                    current with { Attempts = current.Attempts + 1, NextAt = now + (long)delay.TotalSeconds },
                    AtlasPlacementsJsonContext.Default.AtlasPlacementWork), token).ConfigureAwait(false);
            }

            return true;
        }, static _ => false, token, durable: true).ConfigureAwait(false);
    }

    private static bool Unchanged(StoredRecord? stored, long updated) => stored is not null
        && JsonSerializer.Deserialize(stored.Json.Span, AtlasPlacementsJsonContext.Default.AtlasPlacementWork) is { Updated: var current }
        && current == updated;

    private static string Key(string project, string database) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(project + "\n" + database)));
}

internal readonly union AtlasOutcome(AtlasAcceptance, AtlasRefusal, AtlasRetry);

internal sealed record AtlasAcceptance(IReadOnlyDictionary<(string Project, string Database), string> Results);

internal sealed record AtlasRefusal(int Status);

internal sealed record AtlasRetry;

internal sealed record AtlasPublishRequest(IReadOnlyList<AtlasPlacement> Placements);

internal sealed record AtlasPlacement(long V, string Project, string Database, string? Region, long Updated);

internal sealed record AtlasPublishResponse(IReadOnlyList<AtlasPlacementResult> Placements);

internal sealed record AtlasPlacementResult(string Project, string Database, string Result);

internal sealed record AtlasPlacementWork(string Project, string Database, string? Region, long Updated, int Attempts, long NextAt);

internal sealed record AtlasPlacementStamp(long Updated);

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(AtlasPublishRequest))]
[JsonSerializable(typeof(AtlasPublishResponse))]
[JsonSerializable(typeof(AtlasPlacement))]
[JsonSerializable(typeof(AtlasPlacementResult))]
[JsonSerializable(typeof(AtlasPlacementWork))]
[JsonSerializable(typeof(AtlasPlacementStamp))]
internal sealed partial class AtlasPlacementsJsonContext : JsonSerializerContext;

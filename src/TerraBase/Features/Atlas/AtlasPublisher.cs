using System.Buffers.Text;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using TerraBase.Configuration;
using TerraBase.Features.Catalog;
using TerraBase.Hosting;
using ZLogger;

namespace TerraBase.Features.Atlas;

public sealed record AtlasPlacement(int Version, string Project, string Database, string? Region, long Updated);

public sealed record AtlasPublishRequest(IReadOnlyList<AtlasPlacement> Placements);

public sealed record AtlasPlacementResult(string Project, string Database, string Result);

public sealed record AtlasPublishResponse(IReadOnlyList<AtlasPlacementResult> Placements);

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(AtlasPublishRequest))]
[JsonSerializable(typeof(AtlasPublishResponse))]
internal sealed partial class AtlasJsonContext : JsonSerializerContext;

internal sealed class AtlasPublisher(
    TerraBaseOptions options, IHttpClientFactory http, CatalogStore catalog, TimeProvider time, IMeterFactory meters, ILogger<AtlasPublisher> logger) : BackgroundService
{
    public const string HttpClientName = "atlas-publish";

    private const int Batch = 64;

    private static readonly TimeSpan ResyncInterval = TimeSpan.FromMinutes(10);

    private readonly Channel<AtlasPlacement> _queue = Channel.CreateBounded<AtlasPlacement>(new BoundedChannelOptions(4096)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = true,
    });

    private readonly Counter<long> _publishes = meters.Create(Telemetry.MeterName).CreateCounter<long>("terrabase.atlas.placement_publishes");

    public void Queue(Ulid projectId, string database, string? region)
    {
        if (options.AtlasUrl is null)
        {
            return;
        }

        _queue.Writer.TryWrite(new AtlasPlacement(1, projectId.ToString(), database, region, time.GetUtcNow().ToUnixTimeMilliseconds()));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (options.AtlasUrl is not { } url || options.AtlasPublishKey is not { } key)
        {
            return;
        }

        var nextResync = DateTimeOffset.MinValue;
        while (!stoppingToken.IsCancellationRequested)
        {
            if (time.GetUtcNow() >= nextResync)
            {
                nextResync = time.GetUtcNow() + ResyncInterval;
                await ResyncAsync(stoppingToken);
            }

            using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(30), time);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, wait.Token);
            List<AtlasPlacement> batch = [];
            if (await WaitAsync(linked.Token))
            {
                while (batch.Count < Batch && _queue.Reader.TryRead(out var placement))
                {
                    batch.Add(placement);
                }
            }

            if (batch.Count > 0 && !await PublishAsync(url, key, batch, stoppingToken))
            {
                foreach (var placement in batch)
                {
                    _queue.Writer.TryWrite(placement);
                }

                await Task.Delay(TimeSpan.FromSeconds(5), time, stoppingToken);
            }
        }
    }

    private async Task<bool> WaitAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _queue.Reader.WaitToReadAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested || !_queue.Reader.Completion.IsCompleted)
        {
            return false;
        }
    }

    private async Task ResyncAsync(CancellationToken cancellationToken)
    {
        string? after = null;
        while (await catalog.AllDatabasesAsync(after, 256, cancellationToken) switch
        {
            IReadOnlyList<DatabaseRecord> records => records,
            BackendUnavailable => [],
        } is { Count: > 0 } page)
        {
            foreach (var database in page.Where(database => database.PlacedIn(options.Region)))
            {
                Queue(database.ProjectId, database.Name, options.Region);
            }

            after = page[^1].Id.ToString();
        }
    }

    private async Task<bool> PublishAsync(Uri url, ECDsa key, List<AtlasPlacement> placements, CancellationToken cancellationToken)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(new AtlasPublishRequest(placements), AtlasJsonContext.Default.AtlasPublishRequest);
        var stamp = time.GetUtcNow().ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var signed = Encoding.UTF8.GetBytes(string.Concat("atlas-publish-v1\n", stamp, "\n")).Concat(body).ToArray();
        var signature = key.SignData(signed, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(url, "v1/publish")) { Content = new ByteArrayContent(body) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Headers.Add("X-Atlas-Timestamp", stamp);
        request.Headers.Add("X-Atlas-Signature", Base64Url.EncodeToString(signature));
        try
        {
            using var response = await http.CreateClient(HttpClientName).SendAsync(request, cancellationToken);
            var refused = (int)response.StatusCode is >= 400 and < 500 and not 408 and not 429;
            _publishes.Add(placements.Count, new KeyValuePair<string, object?>("result", response.IsSuccessStatusCode ? "updated" : refused ? "refused" : "retryable"));
            if (refused)
            {
                logger.ZLogError($"Atlas refused placement publish with status {(int)response.StatusCode}; dropping {placements.Count} placements");
            }

            return response.IsSuccessStatusCode || refused;
        }
        catch (Exception error) when (error is HttpRequestException || (error is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            logger.ZLogWarning($"Atlas placement publish failed; retrying");
            _publishes.Add(placements.Count, new KeyValuePair<string, object?>("result", "retryable"));
            return false;
        }
    }
}

using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using ZLogger;

namespace TerraScale.Platform.Features.Shapes;

public sealed record CdnPurgeRequest(IReadOnlyList<string> Files);

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(CdnPurgeRequest))]
public sealed partial class CdnJsonContext : JsonSerializerContext;

public sealed class ShapeCdn(IHttpClientFactory httpClients, PlatformOptions options, PlatformMetrics metrics, ILogger<ShapeCdn> logger) : BackgroundService
{
    public const string HttpClientName = "cloudflare";
    private const int BatchUrls = 100;
    private const int QueuedUrls = 8192;
    private readonly Channel<string> _urls = Channel.CreateBounded<string>(new BoundedChannelOptions(QueuedUrls)
    {
        FullMode = BoundedChannelFullMode.Wait,
        SingleReader = true,
    });

    public async ValueTask QueueAsync(IReadOnlyList<string> urls, CancellationToken cancellationToken)
    {
        if (options.Shapes is null)
        {
            return;
        }

        foreach (var url in urls)
        {
            await _urls.Writer.WriteAsync(url, cancellationToken);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (options.Shapes is not { } shapes)
        {
            return;
        }

        await foreach (var first in _urls.Reader.ReadAllAsync(stoppingToken))
        {
            List<string> batch = [first];
            while (batch.Count < BatchUrls && _urls.Reader.TryRead(out var next))
            {
                batch.Add(next);
            }

            var accepted = await PurgeAsync(shapes, batch, stoppingToken);
            metrics.ShapePurge(accepted);
            if (!accepted)
            {
                logger.ZLogWarning($"Cloudflare refused purging {batch.Count} shape URLs");
            }
        }
    }

    private async ValueTask<bool> PurgeAsync(ShapeStorageOptions shapes, IReadOnlyList<string> urls, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"zones/{Uri.EscapeDataString(shapes.CdnZone)}/purge_cache")
        {
            Content = JsonContent.Create(new CdnPurgeRequest(urls), CdnJsonContext.Default.CdnPurgeRequest),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", shapes.CdnApiToken);
        try
        {
            using var response = await httpClients.CreateClient(HttpClientName).SendAsync(request, cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }
}

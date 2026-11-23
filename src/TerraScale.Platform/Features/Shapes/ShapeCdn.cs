using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace TerraScale.Platform.Features.Shapes;

public sealed record CdnPurgeRequest(IReadOnlyList<string> Files);

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(CdnPurgeRequest))]
public sealed partial class CdnJsonContext : JsonSerializerContext;

public sealed class ShapeCdn(IHttpClientFactory httpClients, PlatformOptions options, PlatformMetrics metrics)
{
    public const string HttpClientName = "cloudflare";
    private const int BatchUrls = 100;

    public async ValueTask<Result<Done, Unavailable>> PurgeAsync(IReadOnlyList<string> urls, CancellationToken cancellationToken)
    {
        if (options.Shapes is not { } shapes || urls.Count == 0)
        {
            return new Done();
        }

        foreach (var batch in urls.Chunk(BatchUrls))
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"zones/{Uri.EscapeDataString(shapes.CdnZone)}/purge_cache")
            {
                Content = JsonContent.Create(new CdnPurgeRequest(batch), CdnJsonContext.Default.CdnPurgeRequest),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", shapes.CdnApiToken);
            var accepted = await SendAsync(request, cancellationToken);
            metrics.ShapePurge(accepted);
            if (!accepted)
            {
                return new Unavailable("Cloudflare refused the shape cache purge", "cdn_purge_failed");
            }
        }

        return new Done();
    }

    private async ValueTask<bool> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
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

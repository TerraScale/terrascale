using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace TerraBase.Testing;

/// <summary>
/// The Cloudflare purge API behind the shape CDN (ShapeCdnPurge), routed in place of api.cloudflare.com: it checks the
/// bearer token and zone, records every purged URL and answers success.
/// </summary>
public sealed class FakeCloudflare : IDisposable
{
    /// <summary>The API token a server configured by <see cref="FakeS3"/> with <c>R2 = true</c> sends.</summary>
    public const string Token = "test-cloudflare-token";

    private const string PurgePath = "/client/v4/zones/4cc466e19478c116fae7dc445a6d0911/purge_cache";
    private readonly ConcurrentQueue<string> _purged = new();
    private readonly SemaphoreSlim _requests = new(0);

    /// <summary>Every URL purged so far, in request order.</summary>
    public IReadOnlyCollection<string> Purged => _purged;

    public void Dispose() => _requests.Dispose();

    /// <summary>Routes the server's Cloudflare API client to this fake.</summary>
    public void Route(IServiceCollection services) =>
        services.AddHttpClient("cloudflare-api").ConfigurePrimaryHttpMessageHandler(() => new Handler(this));

    /// <summary>Waits until at least <paramref name="count"/> URLs are purged; the caller's token bounds the wait.</summary>
    public async Task PurgedAsync(int count, CancellationToken cancellationToken)
    {
        while (_purged.Count < count)
        {
            await _requests.WaitAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
        }
    }

    private async Task<HttpResponseMessage> RespondAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Method != HttpMethod.Post || request.RequestUri?.AbsoluteUri != "https://api.cloudflare.com" + PurgePath)
        {
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        if (request.Headers.Authorization is not { Scheme: "Bearer", Parameter: Token })
        {
            return new HttpResponseMessage(HttpStatusCode.Forbidden);
        }

        using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
        foreach (var file in body.RootElement.GetProperty("files").EnumerateArray())
        {
            _purged.Enqueue(file.GetString()!);
        }

        _ = _requests.Release();
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"success":true,"errors":[],"messages":[]}""", Encoding.UTF8, "application/json") };
    }

    private sealed class Handler(FakeCloudflare cloudflare) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            cloudflare.RespondAsync(request, cancellationToken);
    }
}

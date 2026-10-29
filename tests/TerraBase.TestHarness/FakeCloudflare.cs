using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace TerraBase.Testing;

/// <summary>
/// The Cloudflare API behind the shape CDN, routed in place of api.cloudflare.com: purge (ShapeCdnPurge) records every
/// purged URL, and zone analytics (ShapeCdnAnalytics) answers the configured cache-status groups. Both check the bearer
/// token and the zone.
/// </summary>
public sealed class FakeCloudflare : IDisposable
{
    /// <summary>The API token a server configured by <see cref="FakeS3"/> with <c>R2 = true</c> sends.</summary>
    public const string Token = "test-cloudflare-token";

    private const string Zone = "4cc466e19478c116fae7dc445a6d0911";
    private const string Api = "https://api.cloudflare.com/client/v4";
    private readonly ConcurrentQueue<string> _purged = new();
    private readonly SemaphoreSlim _requests = new(0);
    private readonly ConcurrentQueue<(string Host, string Start, string End)> _windows = new();
    private readonly SemaphoreSlim _queries = new(0);

    /// <summary>Every URL purged so far, in request order.</summary>
    public IReadOnlyCollection<string> Purged => _purged;

    /// <summary>The (cache status, count) groups every analytics query answers.</summary>
    public IReadOnlyList<(string CacheStatus, long Count)> Analytics { get; set; } = [];

    /// <summary>Every analytics query's host and half-open window, in request order.</summary>
    public IReadOnlyCollection<(string Host, string Start, string End)> Windows => _windows;

    public void Dispose()
    {
        _requests.Dispose();
        _queries.Dispose();
    }

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

    /// <summary>Waits until at least <paramref name="count"/> analytics queries arrived; the caller's token bounds the wait.</summary>
    public async Task QueriedAsync(int count, CancellationToken cancellationToken)
    {
        while (_windows.Count < count)
        {
            await _queries.WaitAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
        }
    }

    private async Task<HttpResponseMessage> RespondAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Method != HttpMethod.Post)
        {
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        if (request.Headers.Authorization is not { Scheme: "Bearer", Parameter: Token })
        {
            return new HttpResponseMessage(HttpStatusCode.Forbidden);
        }

        using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
        return request.RequestUri?.AbsoluteUri switch
        {
            Api + "/zones/" + Zone + "/purge_cache" => Purge(body.RootElement),
            Api + "/graphql" => Query(body.RootElement.GetProperty("variables")),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        };
    }

    private HttpResponseMessage Purge(JsonElement body)
    {
        foreach (var file in body.GetProperty("prefixes").EnumerateArray())
        {
            _purged.Enqueue(file.GetString()!);
        }

        _ = _requests.Release();
        return Answer("""{"success":true,"errors":[],"messages":[]}""");
    }

    private HttpResponseMessage Query(JsonElement variables)
    {
        if (variables.GetProperty("zone").GetString() != Zone)
        {
            return Answer("""{"data":null,"errors":[{"message":"unknown zone"}]}""");
        }

        _windows.Enqueue((variables.GetProperty("host").GetString()!, variables.GetProperty("start").GetString()!, variables.GetProperty("end").GetString()!));
        _ = _queries.Release();
        var groups = string.Join(",", Analytics.Select(group => $$$"""{"count":{{{group.Count}}},"dimensions":{"cacheStatus":"{{{group.CacheStatus}}}"}}"""));
        return Answer($$$"""{"data":{"viewer":{"zones":[{"httpRequestsAdaptiveGroups":[{{{groups}}}]}]}},"errors":null}""");
    }

    private static HttpResponseMessage Answer(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class Handler(FakeCloudflare cloudflare) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            cloudflare.RespondAsync(request, cancellationToken);
    }
}

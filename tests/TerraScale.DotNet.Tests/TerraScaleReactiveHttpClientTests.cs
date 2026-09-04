using System.Net;
using System.Text;

namespace TerraScale.DotNet.Tests;

public sealed class TerraScaleReactiveHttpClientTests
{
    [Test]
    public async Task AttachDownloadsImmutableSnapshotAndPollsAtCurrentOffset()
    {
        using var handler = new ReactiveHandler();
        using var httpClient = new HttpClient(handler);
        var client = new TerraScaleReactiveHttpClient(
            httpClient,
            new TerraScaleReactiveHttpClientOptions
            {
                BaseAddress = new Uri("https://sync.api.terrascale.tech"),
                BearerToken = "token",
                MaxRetryAttempts = 0,
            });

        await using var subscription = await client.SubscribeAsync("SELECT * FROM docs", "tenant-a");
        var result = await subscription.ReadNextAsync();

        await Assert.That(handler.Requests.Count).IsEqualTo(3);
        await Assert.That(handler.Requests[0].Method).IsEqualTo(HttpMethod.Post);
        await Assert.That(handler.Requests[0].Headers["Authorization"]).IsEqualTo("Bearer token");
        await Assert.That(handler.Requests[1].Headers["X-TerraScale-Capability"]).IsEqualTo("capability");
        await Assert.That(handler.Requests[2].Uri.Query).IsEqualTo("?offset=0");
        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Frame).IsTypeOf<TerraScaleReactiveDelta>();
        await Assert.That(subscription.Offset).IsEqualTo(1ul);
        await Assert.That(result.ETag).IsEqualTo("\"revision-1\"");
    }

    [Test]
    public async Task EmptyNoStorePollReturnsNullWithoutAdvancingOffset()
    {
        using var handler = new ReactiveHandler(timeout: true);
        using var httpClient = new HttpClient(handler);
        var client = new TerraScaleReactiveHttpClient(
            httpClient,
            new TerraScaleReactiveHttpClientOptions { BaseAddress = new Uri("https://sync.api.terrascale.tech") });

        await using var subscription = await client.SubscribeAsync("SELECT * FROM docs", "scope");
        var result = await subscription.ReadNextAsync();

        await Assert.That(result).IsNull();
        await Assert.That(subscription.Offset).IsEqualTo(0ul);
    }

    [Test]
    public async Task CancellationStopsAnInFlightPoll()
    {
        using var handler = new ReactiveHandler(blockPoll: true);
        using var httpClient = new HttpClient(handler);
        var client = new TerraScaleReactiveHttpClient(
            httpClient,
            new TerraScaleReactiveHttpClientOptions { BaseAddress = new Uri("https://sync.api.terrascale.tech") });
        await using var subscription = await client.SubscribeAsync("SELECT * FROM docs", "scope");
        using var cancellation = new CancellationTokenSource();
        var pending = subscription.ReadNextAsync(cancellation.Token);
        await handler.PollStarted.Task;
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() => pending);
    }

    private sealed class ReactiveHandler(bool timeout = false, bool blockPoll = false) : HttpMessageHandler
    {
        public List<RequestObservation> Requests { get; } = [];
        public TaskCompletionSource PollStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(new RequestObservation(request.Method, request.RequestUri!, request.Headers.ToDictionary(pair => pair.Key, pair => pair.Value.Single())));
            if (request.Method == HttpMethod.Post)
            {
                return Json("{\"view\":\"view-1\",\"generation\":1,\"snapshot_chunks\":[\"/api/v1/reactive/views/view-1/1/snapshot/0\"],\"log\":\"/api/v1/reactive/views/view-1/1/log?offset=0\",\"checkpoint\":\"checkpoint-0\",\"capability\":\"capability\",\"capability_header\":\"X-TerraScale-Capability\",\"offset\":0}");
            }

            if (request.RequestUri!.AbsolutePath.EndsWith("/snapshot/0", StringComparison.Ordinal))
            {
                var response = Binary([0x82, 0xa3, (byte)'c', (byte)'u', (byte)'t', 0x00, 0xa4, (byte)'d', (byte)'o', (byte)'c', (byte)'s', 0x90]);
                response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"snapshot-0\"");
                response.Headers.TryAddWithoutValidation("Cloudflare-CDN-Cache-Control", "public, max-age=60, immutable");
                return response;
            }

            PollStarted.TrySetResult();
            if (blockPoll) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            if (timeout)
            {
                var response = Binary([0x90]);
                response.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoStore = true };
                return response;
            }

            var body = new List<byte> { 0x83, 0xa8 };
            body.AddRange(Encoding.UTF8.GetBytes("revision"));
            body.Add(0x01);
            body.Add(0xa3);
            body.AddRange(Encoding.UTF8.GetBytes("cut"));
            body.Add(0x01);
            body.Add(0xa6);
            body.AddRange(Encoding.UTF8.GetBytes("deltas"));
            body.Add(0x91);
            body.Add(0x83); body.Add(0xa2); body.AddRange(Encoding.UTF8.GetBytes("id")); body.Add(0xa1); body.Add((byte)'a'); body.Add(0xa3); body.AddRange(Encoding.UTF8.GetBytes("rev")); body.Add(0x01); body.Add(0xa4); body.AddRange(Encoding.UTF8.GetBytes("body")); body.Add(0xc4); body.Add(0x02); body.AddRange("{}"u8.ToArray());
            var log = Binary(body.ToArray());
            log.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"revision-1\"");
            return log;
        }

        private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        private static HttpResponseMessage Binary(byte[] body) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };
    }

    private sealed record RequestObservation(HttpMethod Method, Uri Uri, IReadOnlyDictionary<string, string> Headers);
}

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace TerraScale.DotNet.Tests;

public sealed class TerraScaleReactiveHttpClientTests
{
    [Test]
    public async Task CanonicalAttachUsesDatabaseStatementParametersAndSafeUrls()
    {
        using var handler = new ReactiveHandler(subscription: "orders");
        using var httpClient = new HttpClient(handler);
        var client = new TerraScaleReactiveHttpClient(httpClient, new TerraScaleReactiveHttpClientOptions
        {
            BaseAddress = new Uri("https://sync.api.terrascale.tech"),
            ApiBaseAddress = new Uri("https://member.api.terrascale.tech"),
            BearerToken = "token",
            RegionHint = "gru",
            MaxRetryAttempts = 0,
        });

        await using var subscription = await client.SubscribeAsync(
            "db-1", "orders", "SELECT * FROM orders WHERE id = ?", new byte[] { 0, 255, 1 });
        var result = await subscription.ReadNextAsync();
        var attach = JsonSerializer.Deserialize<JsonElement>(handler.AttachBody!);

        await Assert.That(attach.GetProperty("databaseId").GetString()).IsEqualTo("db-1");
        await Assert.That(attach.GetProperty("subscription").GetString()).IsEqualTo("orders");
        await Assert.That(attach.GetProperty("statement").GetString()).IsEqualTo("SELECT * FROM orders WHERE id = ?");
        await Assert.That(attach.GetProperty("parameters").GetString()).IsEqualTo("AP8B");
        await Assert.That(attach.GetProperty("checkpoint").ValueKind).IsEqualTo(JsonValueKind.Null);
        await Assert.That(attach.GetProperty("transport").GetString()).IsEqualTo("http");
        await Assert.That(handler.Requests[0].Headers["Authorization"]).IsEqualTo("Bearer token");
        await Assert.That(handler.Requests[0].Headers["X-TerraScale-Region-Hint"]).IsEqualTo("gru");
        await Assert.That(handler.Requests[1].Uri.AbsoluteUri).IsEqualTo("https://sync.api.terrascale.tech/api/v1/reactive/views/view-1/3/snapshot/0");
        await Assert.That(handler.Requests[1].Headers["X-TerraScale-View-Capability"]).IsEqualTo("capability");
        await Assert.That(handler.Requests[2].Uri.Query).IsEqualTo("?offset=0");
        await Assert.That(handler.Requests[2].Headers["X-TerraScale-View-Capability"]).IsEqualTo("capability");
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
        var client = new TerraScaleReactiveHttpClient(httpClient, new TerraScaleReactiveHttpClientOptions { MaxRetryAttempts = 0 });
        await using var subscription = await client.SubscribeAsync("db", "sub", "SELECT * FROM docs");
        var result = await subscription.ReadNextAsync();
        await Assert.That(result).IsNull();
        await Assert.That(subscription.Offset).IsEqualTo(0ul);
    }

    [Test]
    public async Task LegacyMapFrameIsRejectedFailClosed()
    {
        using var handler = new ReactiveHandler(legacyFrame: true);
        using var httpClient = new HttpClient(handler);
        var client = new TerraScaleReactiveHttpClient(httpClient, new TerraScaleReactiveHttpClientOptions { MaxRetryAttempts = 0 });
        await using var subscription = await client.SubscribeAsync("db", "sub", "SELECT * FROM docs");
        await Assert.ThrowsAsync<TerraScaleReactiveProtocolException>(() => subscription.ReadNextAsync());
    }

    [Test]
    public async Task CancellationStopsAnInFlightPoll()
    {
        using var handler = new ReactiveHandler(blockPoll: true);
        using var httpClient = new HttpClient(handler);
        var client = new TerraScaleReactiveHttpClient(httpClient, new TerraScaleReactiveHttpClientOptions { MaxRetryAttempts = 0 });
        await using var subscription = await client.SubscribeAsync("db", "sub", "SELECT * FROM docs");
        using var cancellation = new CancellationTokenSource();
        var pending = subscription.ReadNextAsync(cancellation.Token);
        await handler.PollStarted.Task;
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => pending);
    }

    [Test]
    public async Task HostileFeatureAndMapFramesAreRejected()
    {
        foreach (var frame in new[] { Envelope(6, body: [0x90], features: 1), new byte[] { 0x81, 0xa1, (byte)'x', 0x01 } })
        {
            using var handler = new ReactiveHandler(pollFrame: frame);
            using var httpClient = new HttpClient(handler);
            var client = new TerraScaleReactiveHttpClient(httpClient, new TerraScaleReactiveHttpClientOptions { MaxRetryAttempts = 0 });
            await using var subscription = await client.SubscribeAsync("db", "sub", "SELECT * FROM docs");
            await Assert.ThrowsAsync<TerraScaleReactiveProtocolException>(() => subscription.ReadNextAsync());
        }
    }

    [Test]
    public async Task BackpressureAndRegionFramesChangeSessionState()
    {
        using var handler = new ReactiveHandler(pollFrame: Envelope(11, body: [0x91, 0x01]));
        using var httpClient = new HttpClient(handler);
        var client = new TerraScaleReactiveHttpClient(httpClient, new TerraScaleReactiveHttpClientOptions { MaxRetryAttempts = 0 });
        await using var subscription = await client.SubscribeAsync("db", "sub", "SELECT * FROM docs");
        var paused = await subscription.ReadNextAsync();
        await Assert.That(paused!.Frame).IsTypeOf<TerraScaleReactiveBackpressure>();
        await Assert.That(subscription.Paused).IsTrue();

        using var regionHandler = new ReactiveHandler(pollFrame: Envelope(9, body: [0x91, 0xa3, (byte)'g', (byte)'r', (byte)'u']));
        using var regionHttpClient = new HttpClient(regionHandler);
        var regionClient = new TerraScaleReactiveHttpClient(regionHttpClient, new TerraScaleReactiveHttpClientOptions { MaxRetryAttempts = 0 });
        await using var regionSubscription = await regionClient.SubscribeAsync("db", "sub", "SELECT * FROM docs");
        var route = await regionSubscription.ReadNextAsync();
        await Assert.That(route!.Frame).IsTypeOf<TerraScaleReactiveRouteChange>();
        await Assert.That(regionSubscription.SessionState).IsEqualTo(TerraScaleViewSessionState.Live);
    }

    private sealed class ReactiveHandler(bool timeout = false, bool blockPoll = false, bool legacyFrame = false, byte[]? pollFrame = null, string subscription = "sub") : HttpMessageHandler
    {
        public List<RequestObservation> Requests { get; } = [];
        public TaskCompletionSource PollStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string? AttachBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var headers = request.Headers.ToDictionary(pair => pair.Key, pair => pair.Value.Single());
            Requests.Add(new RequestObservation(request.Method, request.RequestUri!, headers));
            if (request.Method == HttpMethod.Post)
            {
                AttachBody = await request.Content!.ReadAsStringAsync(cancellationToken);
                return Json("{\"viewId\":\"view-1\",\"generation\":3,\"snapshotUrls\":[\"https://attacker.invalid/not-canonical\"],\"logUrl\":\"https://attacker.invalid/log\",\"checkpoint\":\"Y3AtMA==\",\"capability\":\"capability\"}");
            }
            if (request.RequestUri!.AbsolutePath.EndsWith("/snapshot/0", StringComparison.Ordinal))
            {
                var response = Binary(Envelope(3, 0, SnapshotMessage(subscription, Snapshot(3, 0, 0, "Y3AtMA=="))));
                response.Headers.ETag = new EntityTagHeaderValue("\"snapshot-0\"");
                return response;
            }
            PollStarted.TrySetResult();
            if (blockPoll) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            if (timeout)
            {
                var response = Binary([]);
                response.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true };
                return response;
            }
            var body = pollFrame ?? (legacyFrame ? new byte[] { 0x81, 0xa1, (byte)'x', 0x01 } : Envelope(4, 0, Delta(subscription, 3, 0, 1, "Y3AtMQ==")));
            var poll = Binary(body);
            poll.Headers.ETag = new EntityTagHeaderValue("\"revision-1\"");
            return poll;
        }

        private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        private static HttpResponseMessage Binary(byte[] body) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };
    }

    private sealed record RequestObservation(HttpMethod Method, Uri Uri, IReadOnlyDictionary<string, string> Headers);

    private static byte[] Envelope(uint tag, ulong correlation = 0, byte[]? body = null, uint features = 0)
    {
        var result = new List<byte> { 0x97, 0x02, 0x01, (byte)features, (byte)tag, 0, 0 };
        result.AddRange(body ?? Array.Empty<byte>());
        return result.ToArray();
    }

    private static byte[] Snapshot(uint generation, uint revision, uint auth, string checkpoint)
    {
        var bytes = Convert.FromBase64String(checkpoint);
        var result = new List<byte> { 0x96, (byte)generation, (byte)auth, (byte)revision, 0x90, 0xa1, (byte)'d', 0xc4, (byte)bytes.Length };
        result.AddRange(bytes);
        return result.ToArray();
    }

    private static byte[] Delta(string subscription, uint generation, uint from, uint to, string checkpoint)
    {
        var bytes = Convert.FromBase64String(checkpoint);
        var result = new List<byte> { 0x97, (byte)(0xa0 | subscription.Length) };
        result.AddRange(Encoding.UTF8.GetBytes(subscription));
        result.AddRange(new byte[] { (byte)generation, (byte)from, (byte)to, 0x90, 0xa1, (byte)'d', 0xc4, (byte)bytes.Length });
        result.AddRange(bytes);
        return result.ToArray();
    }

    private static byte[] SnapshotMessage(string subscription, byte[] snapshot)
    {
        var result = new List<byte> { 0x92, (byte)(0xa0 | subscription.Length) };
        result.AddRange(Encoding.UTF8.GetBytes(subscription));
        result.AddRange(snapshot);
        return result.ToArray();
    }
}

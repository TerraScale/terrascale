using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace TerraScale.DotNet.Tests;

public sealed class TerraScaleReactiveSseTests
{
    [Test]
    public async Task SseDataEventDecodesTheSamePinnedDeltaAsLongPollAndAdvancesOffset()
    {
        using var corpus = await ParseCorpusAsync();
        using var handler = new SseCorpusHandler(corpus.RootElement, log => Sse($"id: 1\ndata: {Convert.ToBase64String(log)}\n\n"));
        using var http = new HttpClient(handler);
        var client = new TerraScaleReactiveHttpClient(http, new TerraScaleReactiveHttpClientOptions { MaxRetryAttempts = 0 });
        await using var longPoll = await client.SubscribeAsync("demo", "v-conformance-view", "SELECT id FROM orders ORDER BY id");
        var longPollResult = await longPoll.ReadNextAsync();

        await using var sse = await client.SubscribeAsync("demo", "v-conformance-view", "SELECT id FROM orders ORDER BY id", transport: TerraScaleReactiveTransport.ServerSentEvents);
        var sseResult = await sse.ReadNextAsync();

        await Assert.That(longPollResult!.Frame).IsTypeOf<TerraScaleReactiveDelta>();
        await Assert.That(sseResult!.Frame).IsTypeOf<TerraScaleReactiveDelta>();
        var expected = (TerraScaleReactiveDelta)longPollResult.Frame;
        var actual = (TerraScaleReactiveDelta)sseResult.Frame;
        await Assert.That(actual.ViewGeneration).IsEqualTo(expected.ViewGeneration);
        await Assert.That(actual.FromRevision).IsEqualTo(expected.FromRevision);
        await Assert.That(actual.ToRevision).IsEqualTo(expected.ToRevision);
        await Assert.That(actual.Digest).IsEqualTo(expected.Digest);
        await Assert.That(actual.Checkpoint).IsEqualTo(expected.Checkpoint);
        await Assert.That(actual.Operations.Count).IsEqualTo(expected.Operations.Count);
        await Assert.That(actual.Operations.Count).IsEqualTo(1);
        var expectedInsert = (TerraScaleReactiveInsert)expected.Operations[0];
        var actualInsert = (TerraScaleReactiveInsert)actual.Operations[0];
        await Assert.That(actualInsert.Position).IsEqualTo(expectedInsert.Position);
        await Assert.That(actualInsert.Item.Identity).IsEqualTo(expectedInsert.Item.Identity);
        await Assert.That(actualInsert.Item.Fields.Count).IsEqualTo(expectedInsert.Item.Fields.Count);
        await Assert.That(actualInsert.Item.Fields[0].Path).IsEqualTo(expectedInsert.Item.Fields[0].Path);
        await Assert.That(actualInsert.Item.Fields[0].Value.Span.SequenceEqual(expectedInsert.Item.Fields[0].Value.Span)).IsTrue();
        await Assert.That(sse.Offset).IsEqualTo(longPoll.Offset);
        await Assert.That(sse.Offset).IsEqualTo(1ul);
        await Assert.That(sseResult.ETag).IsNull();
        await Assert.That(longPollResult.ETag).IsEqualTo("\"v-conformance-view-1-1\"");
    }

    [Test]
    public async Task SseStreamRequestUsesCanonicalRouteAcceptCapabilityAndAuthHeaders()
    {
        using var corpus = await ParseCorpusAsync();
        using var handler = new SseCorpusHandler(corpus.RootElement, log => Sse($"id: 1\ndata: {Convert.ToBase64String(log)}\n\n"));
        using var http = new HttpClient(handler);
        var client = new TerraScaleReactiveHttpClient(http, new TerraScaleReactiveHttpClientOptions { MaxRetryAttempts = 0, BearerToken = "token" });
        await using var subscription = await client.SubscribeAsync("demo", "v-conformance-view", "SELECT id FROM orders ORDER BY id", transport: TerraScaleReactiveTransport.ServerSentEvents);
        await subscription.ReadNextAsync();

        var streamRequest = handler.Requests.Single(request => request.Uri.AbsolutePath.EndsWith("/stream", StringComparison.Ordinal));
        await Assert.That(streamRequest.Method).IsEqualTo(HttpMethod.Get);
        await Assert.That(streamRequest.Uri.AbsoluteUri).IsEqualTo("https://sync.api.terrascale.tech/api/v1/reactive/views/v-conformance-view/1/stream?offset=0");
        await Assert.That(streamRequest.Headers["Accept"]).IsEqualTo("text/event-stream");
        await Assert.That(streamRequest.Headers["X-TerraScale-Capability"]).IsEqualTo(CorpusCapability);
        await Assert.That(streamRequest.Headers["Authorization"]).IsEqualTo("Bearer token");
    }

    [Test]
    public async Task SsePingCommentsAreIgnoredUntilTheNextDataEvent()
    {
        using var corpus = await ParseCorpusAsync();
        using var handler = new SseCorpusHandler(corpus.RootElement, log => Sse($": ping\n\n: ping\n\nid: 1\ndata: {Convert.ToBase64String(log)}\n\n"));
        using var http = new HttpClient(handler);
        var client = new TerraScaleReactiveHttpClient(http, new TerraScaleReactiveHttpClientOptions { MaxRetryAttempts = 0 });
        await using var subscription = await client.SubscribeAsync("demo", "v-conformance-view", "SELECT id FROM orders ORDER BY id", transport: TerraScaleReactiveTransport.ServerSentEvents);

        var result = await subscription.ReadNextAsync();

        await Assert.That(result!.Frame).IsTypeOf<TerraScaleReactiveDelta>();
        await Assert.That(subscription.Offset).IsEqualTo(1ul);
        await Assert.That(subscription.CurrentSnapshot.Items.Count).IsEqualTo(1);
    }

    [Test]
    public async Task SseErrorEventEndsTheStreamAndReattachesOnTheNextPoll()
    {
        using var corpus = await ParseCorpusAsync();
        using var handler = new SseCorpusHandler(corpus.RootElement, _ => Sse("event: error\ndata: c2xvd19jb25zdW1lcg==\n\n"));
        using var http = new HttpClient(handler);
        var client = new TerraScaleReactiveHttpClient(http, new TerraScaleReactiveHttpClientOptions { MaxRetryAttempts = 0 });
        await using var subscription = await client.SubscribeAsync("demo", "v-conformance-view", "SELECT id FROM orders ORDER BY id", transport: TerraScaleReactiveTransport.ServerSentEvents);

        var terminated = await subscription.ReadNextAsync();
        var reattached = await subscription.ReadNextAsync();

        await Assert.That(terminated).IsNull();
        await Assert.That(reattached!.Frame).IsTypeOf<TerraScaleReactiveReset>();
        await Assert.That(((TerraScaleReactiveReset)reattached.Frame).Reason).IsEqualTo(TerraScaleReactiveResetReason.RegionMoved);
        await Assert.That(handler.AttachPosts).IsEqualTo(2);
        await Assert.That(subscription.SessionState).IsEqualTo(TerraScaleViewSessionState.Live);
    }

    [Test]
    public async Task SseStreamEndingMidEventMarksSubscriptionForReattach()
    {
        using var corpus = await ParseCorpusAsync();
        using var handler = new SseCorpusHandler(corpus.RootElement, log => Sse($"id: 1\ndata: {Convert.ToBase64String(log)}\n"));
        using var http = new HttpClient(handler);
        var client = new TerraScaleReactiveHttpClient(http, new TerraScaleReactiveHttpClientOptions { MaxRetryAttempts = 0 });
        await using var subscription = await client.SubscribeAsync("demo", "v-conformance-view", "SELECT id FROM orders ORDER BY id", transport: TerraScaleReactiveTransport.ServerSentEvents);

        var dropped = await subscription.ReadNextAsync();
        var reattached = await subscription.ReadNextAsync();

        await Assert.That(dropped).IsNull();
        await Assert.That(reattached!.Frame).IsTypeOf<TerraScaleReactiveReset>();
        await Assert.That(handler.AttachPosts).IsEqualTo(2);
        await Assert.That(subscription.Offset).IsEqualTo(0ul);
    }

    [Test]
    public async Task SseConnectionDroppedMidReadMarksSubscriptionForReattach()
    {
        using var corpus = await ParseCorpusAsync();
        using var handler = new SseCorpusHandler(corpus.RootElement, log => Sse(new DroppingStream(Encoding.UTF8.GetBytes($"data: {Convert.ToBase64String(log)}\n"))));
        using var http = new HttpClient(handler);
        var client = new TerraScaleReactiveHttpClient(http, new TerraScaleReactiveHttpClientOptions { MaxRetryAttempts = 0 });
        await using var subscription = await client.SubscribeAsync("demo", "v-conformance-view", "SELECT id FROM orders ORDER BY id", transport: TerraScaleReactiveTransport.ServerSentEvents);

        var dropped = await subscription.ReadNextAsync();
        var reattached = await subscription.ReadNextAsync();

        await Assert.That(dropped).IsNull();
        await Assert.That(reattached!.Frame).IsTypeOf<TerraScaleReactiveReset>();
        await Assert.That(handler.AttachPosts).IsEqualTo(2);
    }

    [Test]
    public async Task SseInvalidBase64DataThrowsProtocolExceptionAndMarksSubscriptionForReattach()
    {
        using var corpus = await ParseCorpusAsync();
        using var handler = new SseCorpusHandler(corpus.RootElement, _ => Sse("id: 1\ndata: %%%not-base64%%%\n\n"));
        using var http = new HttpClient(handler);
        var client = new TerraScaleReactiveHttpClient(http, new TerraScaleReactiveHttpClientOptions { MaxRetryAttempts = 0 });
        await using var subscription = await client.SubscribeAsync("demo", "v-conformance-view", "SELECT id FROM orders ORDER BY id", transport: TerraScaleReactiveTransport.ServerSentEvents);

        var exception = await Assert.ThrowsAsync<TerraScaleReactiveProtocolException>(() => subscription.ReadNextAsync());

        await Assert.That(exception!.Message.Contains("invalid base64", StringComparison.Ordinal)).IsTrue();
        await Assert.That(subscription.Offset).IsEqualTo(0ul);
        await Assert.That(subscription.CurrentSnapshot.Items.Count).IsEqualTo(0);

        var reattached = await subscription.ReadNextAsync();
        await Assert.That(reattached!.Frame).IsTypeOf<TerraScaleReactiveReset>();
        await Assert.That(handler.AttachPosts).IsEqualTo(2);
    }

    private const string CorpusCapability = "v1.v-conformance-view.1.tenant:default:demo.0.1700001000.9bad5fd186ea3e60177d8e214a25562efc38bb388c1821ec0c02cfd5bb8340a6";

    private static async Task<JsonDocument> ParseCorpusAsync()
    {
        await using var stream = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "conformance", "reactive.json"));
        return await JsonDocument.ParseAsync(stream);
    }

    private static HttpResponseMessage Sse(string payload) => new(HttpStatusCode.OK) { Content = new StringContent(payload, Encoding.UTF8, "text/event-stream") };

    private static HttpResponseMessage Sse(Stream stream) => new(HttpStatusCode.OK)
    {
        Content = new StreamContent(stream) { Headers = { ContentType = new MediaTypeHeaderValue("text/event-stream") } },
    };

    /// <summary>Serves the pinned producer corpus for attach, snapshot, and log, and frames the log payload as the SSE script under test.</summary>
    private sealed class SseCorpusHandler(JsonElement corpus, Func<byte[], HttpResponseMessage> streamResponse) : HttpMessageHandler
    {
        private readonly byte[] logBytes = Convert.FromHexString(corpus.GetProperty("log").GetProperty("hex").GetString()!);
        public List<RequestObservation> Requests { get; } = [];
        public int AttachPosts { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(new RequestObservation(request.Method, request.RequestUri!, request.Headers.ToDictionary(pair => pair.Key, pair => pair.Value.Single())));
            if (request.Method == HttpMethod.Post)
            {
                AttachPosts++;
                var fixture = corpus.GetProperty("attach").GetProperty("response");
                return Task.FromResult(new HttpResponseMessage((HttpStatusCode)fixture.GetProperty("status").GetInt32())
                {
                    Content = new StringContent(fixture.GetProperty("body").GetRawText(), Encoding.UTF8, fixture.GetProperty("mediaType").GetString()!),
                });
            }

            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/snapshot/0", StringComparison.Ordinal))
                return Task.FromResult(Binary(corpus.GetProperty("snapshot")));
            if (path.EndsWith("/log", StringComparison.Ordinal))
                return Task.FromResult(Binary(corpus.GetProperty("log")));
            return Task.FromResult(streamResponse(logBytes));
        }

        private static HttpResponseMessage Binary(JsonElement frame)
        {
            var response = new HttpResponseMessage((HttpStatusCode)frame.GetProperty("status").GetInt32())
            {
                Content = new ByteArrayContent(Convert.FromHexString(frame.GetProperty("hex").GetString()!)),
            };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue(frame.GetProperty("mediaType").GetString()!);
            foreach (var header in frame.GetProperty("headers").EnumerateObject())
            {
                if (!response.Headers.TryAddWithoutValidation(header.Name, header.Value.GetString()))
                    response.Content.Headers.TryAddWithoutValidation(header.Name, header.Value.GetString());
            }

            return response;
        }
    }

    private sealed record RequestObservation(HttpMethod Method, Uri Uri, IReadOnlyDictionary<string, string> Headers);

    /// <summary>Yields its prefix once, then fails like a connection reset instead of reporting end-of-stream.</summary>
    private sealed class DroppingStream(byte[] prefix) : Stream
    {
        private int delivered;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => ReadCore(buffer.AsSpan(offset, count));
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken) => ValueTask.FromResult(ReadCore(buffer.Span));
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        private int ReadCore(Span<byte> buffer)
        {
            if (delivered >= prefix.Length) throw new IOException("The SSE connection was dropped mid-event.");
            var count = Math.Min(buffer.Length, prefix.Length - delivered);
            prefix.AsSpan(delivered, count).CopyTo(buffer);
            delivered += count;
            return count;
        }
    }
}

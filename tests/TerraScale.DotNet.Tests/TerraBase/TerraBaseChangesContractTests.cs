using System.Net;
using System.Text;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.DotNet.Tests.TerraBase;

public sealed class TerraBaseChangesContractTests
{
    [Test]
    public async Task Fragmented_UTF8_multiline_data_and_cursor_events_preserve_checkpoints_and_region()
    {
        using var source = new FragmentedStream(Encoding.UTF8.GetBytes(TerraBaseIntegrationFixture.Read("changes.sse")), 1);
        HttpRequestMessage? sent = null;
        using var handler = new TerraBaseTestHandler((request, _) =>
        {
            sent = request;
            return Task.FromResult(Sse(source));
        });
        using var http = new HttpClient(handler);
        var events = new List<TerraBaseChangesEvent>();
        await foreach (var item in Database(http).StreamChangesAsync(cursor: "now", collections: ["items"])) events.Add(item);
        await Assert.That(events.Count).IsEqualTo(4);
        await Assert.That(events[0].Change).IsNull();
        await Assert.That(events[1].Change!.Document!.Value.GetProperty("title").GetString()).IsEqualTo("São Paulo 😀");
        await Assert.That(events[1].Change!.Origin).IsEqualTo("0000000000000001");
        await Assert.That(events[1].Change!.Version).IsEqualTo("2");
        await Assert.That(events[2].Change).IsNull();
        await Assert.That(events[2].Cursor).IsNotEqualTo(events[1].Cursor);
        await Assert.That(events[3].Change!.Document).IsNull();
        await Assert.That(events[3].Change!.Op).IsEqualTo("delete");
        await Assert.That(events.All(item => item.Region == "sjc")).IsTrue();
        await Assert.That(sent!.Headers.Accept.Single().MediaType).IsEqualTo("text/event-stream");
        await Assert.That(sent.RequestUri!.Query).IsEqualTo("?cursor=now&collections=items");
        await Assert.That(source.Disposed).IsTrue();
    }

    [Test]
    public async Task LastEventId_overrides_query_cursor_without_decoding_or_replaying()
    {
        var calls = 0;
        var checkpoint = "AQAAAAAAAAABAAAAAAAAAAI";
        using var handler = new TerraBaseTestHandler(async (request, _) =>
        {
            calls++;
            await Assert.That(request.RequestUri!.Query).IsEqualTo("?collections=items");
            await Assert.That(request.Headers.GetValues("Last-Event-ID").Single()).IsEqualTo(checkpoint);
            return Sse(new MemoryStream(Encoding.UTF8.GetBytes($"event: cursor\nid: {checkpoint}\ndata: {{\"cursor\":\"{checkpoint}\"}}\n\n")));
        });
        using var http = new HttpClient(handler);
        await foreach (var item in Database(http).StreamChangesAsync("now", checkpoint, ["items"]))
            await Assert.That(item.Cursor).IsEqualTo(checkpoint);
        await Assert.That(calls).IsEqualTo(1);
    }

    [Test]
    [Arguments(400, "invalid_cursor", typeof(TerraBaseValidationException))]
    [Arguments(410, "cursor_expired", typeof(TerraBaseCursorExpiredException))]
    [Arguments(429, "changes_busy", typeof(TerraBaseRateLimitedException))]
    [Arguments(401, "unauthorized", typeof(TerraBaseAuthenticationException))]
    public async Task Status_errors_do_not_reset_or_reconnect(int status, string code, Type expected)
    {
        var calls = 0;
        using var handler = new TerraBaseTestHandler((_, _) =>
        {
            calls++;
            var response = TerraBaseIntegrationFixture.Json($$$"""{"error":{"code":"{{{code}}}","message":"refused"}}""", (HttpStatusCode)status);
            response.Headers.Add("X-TerraBase-Region", "sjc");
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(1));
            return Task.FromResult(response);
        });
        using var http = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<TerraBaseException>(() => DrainAsync(Database(http).StreamChangesAsync("opaque")));
        await Assert.That(error!.GetType()).IsEqualTo(expected);
        await Assert.That(error.ErrorCode).IsEqualTo(code);
        await Assert.That(error.Region).IsEqualTo("sjc");
        await Assert.That(error.RetryAfter).IsEqualTo(TimeSpan.FromSeconds(1));
        await Assert.That(calls).IsEqualTo(1);
    }

    [Test]
    [Arguments("cursor_expired", typeof(TerraBaseCursorExpiredException), 410)]
    [Arguments("not_found", typeof(TerraBaseNotFoundException), 404)]
    [Arguments("tenant_memory", typeof(TerraBaseRateLimitedException), 429)]
    [Arguments("storage_busy", typeof(TerraBaseUnavailableException), 503)]
    public async Task Terminal_error_events_surface_after_the_last_checkpoint(string code, Type expected, int status)
    {
        var stream = new FragmentedStream(Encoding.UTF8.GetBytes("event: cursor\nid: AQ\ndata: {\"cursor\":\"AQ\"}\n\n"
            + $"event: error\ndata: {{\"error\":{{\"code\":\"{code}\",\"message\":\"stopped\"}}}}\n\n"), 2);
        using var handler = new TerraBaseTestHandler((_, _) => Task.FromResult(Sse(stream)));
        using var http = new HttpClient(handler);
        await using var enumerator = Database(http).StreamChangesAsync().GetAsyncEnumerator();
        await Assert.That(await enumerator.MoveNextAsync()).IsTrue();
        await Assert.That(enumerator.Current.Cursor).IsEqualTo("AQ");
        var error = await Assert.ThrowsAsync<TerraBaseException>(() => enumerator.MoveNextAsync().AsTask());
        await Assert.That(error!.GetType()).IsEqualTo(expected);
        await Assert.That((int)error.StatusCode!).IsEqualTo(status);
        await Assert.That(error.Region).IsEqualTo("sjc");
        await Assert.That(stream.Disposed).IsTrue();
    }

    [Test]
    public async Task Enumerator_disposal_closes_the_response_without_reading_ahead_or_disposing_the_HttpClient()
    {
        var stream = new FragmentedStream(Encoding.UTF8.GetBytes("event: cursor\nid: AQ\ndata: {\"cursor\":\"AQ\"}\n\n"), 1);
        using var handler = new TerraBaseTestHandler((_, _) => Task.FromResult(Sse(stream)));
        using var http = new HttpClient(handler);
        var enumerator = Database(http).StreamChangesAsync().GetAsyncEnumerator();
        await Assert.That(await enumerator.MoveNextAsync()).IsTrue();
        var reads = stream.Reads;
        await enumerator.DisposeAsync();
        await Assert.That(stream.Reads).IsEqualTo(reads);
        await Assert.That(stream.Disposed).IsTrue();
        // A second request reaches the same caller-owned transport.
        using var response = await http.GetAsync("https://sjc.api.terrascale.tech/healthz", HttpCompletionOption.ResponseHeadersRead);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    [Test]
    public async Task Cancellation_interrupts_a_blocked_stream_read_and_disposes_the_response()
    {
        using var source = new WaitingStream();
        using var handler = new TerraBaseTestHandler((_, _) => Task.FromResult(Sse(source)));
        using var http = new HttpClient(handler);
        using var cancellation = new CancellationTokenSource();
        var read = DrainAsync(Database(http).StreamChangesAsync(cancellationToken: cancellation.Token));
        await source.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await cancellation.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => read);
        await Assert.That(source.Disposed).IsTrue();
    }

    [Test]
    public async Task Stream_lifetime_is_not_the_JSON_request_deadline()
    {
        using var source = new WaitingStream();
        using var handler = new TerraBaseTestHandler((_, _) => Task.FromResult(Sse(source)));
        using var http = new HttpClient(handler);
        using var cancellation = new CancellationTokenSource();
        var timeout = TimeSpan.FromMilliseconds(200);
        var read = DrainAsync(TerraBaseIntegrationFixture.Client(http, timeout).Database(TerraBaseIntegrationFixture.ProjectId, "app").StreamChangesAsync(cancellationToken: cancellation.Token));
        var token = await source.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var acquisitionWindow = new CancellationTokenSource(timeout + timeout);
        await Assert.ThrowsAsync<OperationCanceledException>(() => source.Cancelled.Task.WaitAsync(acquisitionWindow.Token));
        await Assert.That(token.IsCancellationRequested).IsFalse();
        await cancellation.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => read);
    }

    [Test]
    [Arguments("event: cursor\nid: AQ\ndata: {\"cursor\":\"different\"}\n\n")]
    [Arguments("event: change\nid: AQ\ndata: {}\n\n")]
    [Arguments("event: change\ndata: {}\n\n")]
    public async Task Invalid_stream_data_fails_without_advancing_a_checkpoint(string body)
    {
        using var handler = new TerraBaseTestHandler((_, _) => Task.FromResult(Sse(new MemoryStream(Encoding.UTF8.GetBytes(body)))));
        using var http = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<TerraBaseException>(() => DrainAsync(Database(http).StreamChangesAsync()));
        await Assert.That(error!.ErrorCode).IsEqualTo("invalid_response");
        await Assert.That(error.Region).IsEqualTo("sjc");
    }

    [Test]
    public async Task Unbounded_event_is_refused_before_the_parser_can_accumulate_it()
    {
        using var stream = new FragmentedStream(Encoding.UTF8.GetBytes("data: " + new string('x', 4 * 1024 * 1024)), 4096);
        using var handler = new TerraBaseTestHandler((_, _) => Task.FromResult(Sse(stream)));
        using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<InvalidDataException>(() => DrainAsync(Database(http).StreamChangesAsync()));
        await Assert.That(stream.Disposed).IsTrue();
    }

    [Test]
    public async Task The_event_bound_does_not_limit_the_total_stream_size()
    {
        var body = "event: change\nid: AQAAAAAAAAABAAAAAAAAAAI\ndata: {\"collection\":\"items\",\"id\":\"a\",\"op\":\"upsert\","
            + "\"version\":\"2\",\"origin\":\"0000000000000001\",\"document\":{\"title\":\"" + new string('x', 1024 * 1024 - 1024) + "\"}}\n\n";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat(body, 5))));
        using var handler = new TerraBaseTestHandler((_, _) => Task.FromResult(Sse(stream)));
        using var http = new HttpClient(handler);
        var count = 0;
        await foreach (var _ in Database(http).StreamChangesAsync()) count++;
        await Assert.That(count).IsEqualTo(5);
    }

    [Test]
    public async Task Empty_421_preserves_region_and_does_not_follow_Fly_replay()
    {
        var calls = 0;
        using var handler = new TerraBaseTestHandler((_, _) =>
        {
            calls++;
            var response = new HttpResponseMessage(HttpStatusCode.MisdirectedRequest);
            response.Headers.Add("X-TerraBase-Region", "gru");
            response.Headers.Add("fly-replay", "region=sjc");
            return Task.FromResult(response);
        });
        using var http = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<TerraBaseWrongRegionException>(() => DrainAsync(Database(http).StreamChangesAsync()));
        await Assert.That(error!.Region).IsEqualTo("gru");
        await Assert.That(error.ErrorCode).IsNull();
        await Assert.That(calls).IsEqualTo(1);
    }

    [Test]
    public async Task Header_injection_is_rejected_before_any_request()
    {
        using var handler = new TerraBaseTestHandler((_, _) => throw new InvalidOperationException("No request expected"));
        using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<ArgumentException>(() => DrainAsync(Database(http).StreamChangesAsync(lastEventId: "AQ\r\nAuthorization: forged")));
    }

    private static TerraBaseDatabase Database(HttpClient http) => TerraBaseIntegrationFixture.Client(http).Database(TerraBaseIntegrationFixture.ProjectId, "app");
    private static async Task DrainAsync(IAsyncEnumerable<TerraBaseChangesEvent> events)
    {
        await foreach (var _ in events) { }
    }
    private static HttpResponseMessage Sse(Stream source)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(source) };
        response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/event-stream");
        response.Headers.Add("X-TerraBase-Region", "sjc");
        return response;
    }

    private sealed class FragmentedStream(byte[] bytes, int fragment) : MemoryStream(bytes)
    {
        public bool Disposed { get; private set; }
        public int Reads { get; private set; }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Reads++;
            return base.ReadAsync(buffer[..Math.Min(buffer.Length, fragment)], cancellationToken);
        }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }

    private sealed class WaitingStream() : MemoryStream
    {
        public TaskCompletionSource<CancellationToken> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Disposed { get; private set; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Started.TrySetResult(cancellationToken);
            try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
            finally { Cancelled.TrySetResult(); }
            return 0;
        }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
}

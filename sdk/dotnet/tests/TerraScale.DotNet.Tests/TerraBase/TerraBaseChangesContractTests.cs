using System.Net;
using System.Text;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.DotNet.Tests.TerraBase;

public sealed class TerraBaseChangesContractTests
{
    [Test]
    public async Task Transaction_changes_advance_only_on_the_final_event_of_the_group()
    {
        using var source = new FragmentedStream(Encoding.UTF8.GetBytes(TerraBaseIntegrationFixture.Read("transaction-changes.sse")), 1);
        using var handler = new TerraBaseTestHandler((_, _) => Task.FromResult(Sse(source)));
        using var http = new HttpClient(handler);
        var events = new List<TerraBaseChangesEvent>();
        await foreach (var item in Database(http).StreamChangesAsync()) events.Add(item);
        await Assert.That(events.Count).IsEqualTo(3);
        await Assert.That(events[0].Change).IsNull();
        await Assert.That(events[1].Change!.Id).IsEqualTo("a");
        await Assert.That(events[2].Change!.Id).IsEqualTo("b");
        await Assert.That(events[1].Change!.Origin).IsEqualTo(events[2].Change!.Origin);
        await Assert.That(events[1].Change!.Version).IsEqualTo(events[2].Change!.Version);
        await Assert.That(events[1].Cursor).IsEqualTo(events[0].Cursor);
        await Assert.That(events[2].Cursor).IsNotEqualTo(events[0].Cursor);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task An_initial_idless_change_is_delivered_without_fabricating_a_checkpoint(bool resuming)
    {
        var body = TerraBaseIntegrationFixture.Read("transaction-changes.sse");
        body = body[(body.IndexOf("\n\n", StringComparison.Ordinal) + 2)..];
        using var handler = new TerraBaseTestHandler((_, _) => Task.FromResult(Sse(new MemoryStream(Encoding.UTF8.GetBytes(body)))));
        using var http = new HttpClient(handler);
        await using var enumerator = Database(http).StreamChangesAsync(cursor: "now", lastEventId: resuming ? "previous" : null).GetAsyncEnumerator();
        await Assert.That(await enumerator.MoveNextAsync()).IsTrue();
        await Assert.That(enumerator.Current.Change!.Id).IsEqualTo("a");
        await Assert.That(enumerator.Current.Cursor).IsNull();
        await Assert.That(await enumerator.MoveNextAsync()).IsTrue();
        await Assert.That(enumerator.Current.Change!.Id).IsEqualTo("b");
        await Assert.That(enumerator.Current.Cursor).IsNotNull();
        await Assert.That(await enumerator.MoveNextAsync()).IsFalse();
    }

    [Test]
    [Arguments(0, false)]
    [Arguments(1, false)]
    [Arguments(1, true)]
    [Arguments(2, false)]
    public async Task Interrupted_transaction_replays_from_the_last_accepted_checkpoint_and_consumer_deduplicates(int changesBeforeLeaving, bool cancel)
    {
        var body = TerraBaseIntegrationFixture.Read("transaction-changes.sse");
        var calls = 0;
        var credentials = 0;
        var sources = new List<FragmentedStream>();
        string? checkpoint = null;
        using var handler = new TerraBaseTestHandler(async (request, _) =>
        {
            calls++;
            await Assert.That(request.Headers.Authorization!.Parameter).IsEqualTo("action-" + calls);
            if (calls == 1)
            {
                await Assert.That(request.RequestUri!.Query).IsEqualTo("?collections=items");
            }
            else
            {
                await Assert.That(request.Headers.GetValues("Last-Event-ID").Single()).IsEqualTo(checkpoint);
                await Assert.That(request.RequestUri!.Query).IsEqualTo("?collections=items");
            }
            var reply = calls == 1 || changesBeforeLeaving < 2 ? body
                : $"event: cursor\nid: {checkpoint}\ndata: {{\"cursor\":\"{checkpoint}\"}}\n\n";
            var source = new FragmentedStream(Encoding.UTF8.GetBytes(reply), 1);
            sources.Add(source);
            return Sse(source);
        });
        using var http = new HttpClient(handler);
        var db = new TerraBaseDocumentsClient(http, new TerraBaseDocumentsClientOptions
        {
            Region = "sjc",
            AccessTokenProvider = _ => ValueTask.FromResult("action-" + ++credentials),
        }).Database(TerraBaseIntegrationFixture.ProjectId, "app");
        using var cancellation = new CancellationTokenSource();
        var delivered = new List<TerraBaseChange>();
        var applied = new HashSet<(string Origin, string Version, string Collection, string Id)>();
        void Accept(TerraBaseChangesEvent item)
        {
            if (item.Change is { } change)
            {
                delivered.Add(change);
                // This test consumer owns duplicate suppression; the SDK delivers the replay.
                applied.Add((change.Origin, change.Version, change.Collection, change.Id));
            }
            if (item.Cursor is not null) checkpoint = item.Cursor;
        }
        await using (var enumerator = db.StreamChangesAsync(collections: ["items"], cancellationToken: cancellation.Token).GetAsyncEnumerator())
        {
            for (var i = 0; i <= changesBeforeLeaving; i++)
            {
                await Assert.That(await enumerator.MoveNextAsync()).IsTrue();
                Accept(enumerator.Current);
            }
            if (cancel)
            {
                await cancellation.CancelAsync();
                await Assert.ThrowsAsync<OperationCanceledException>(() => enumerator.MoveNextAsync().AsTask());
            }
        }
        await Assert.That(sources[0].Disposed).IsTrue();
        await Assert.That(calls).IsEqualTo(1);
        await foreach (var item in db.StreamChangesAsync(lastEventId: checkpoint, collections: ["items"])) Accept(item);
        await Assert.That(calls).IsEqualTo(2);
        await Assert.That(credentials).IsEqualTo(2);
        await Assert.That(delivered.Count).IsEqualTo(changesBeforeLeaving == 1 ? 3 : 2);
        await Assert.That(applied.Count).IsEqualTo(2);
        await Assert.That(applied.Select(key => key.Id)).IsEquivalentTo(["a", "b"]);
        await Assert.That(sources.All(source => source.Disposed)).IsTrue();
    }

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
    public async Task The_producers_maximum_checkpoint_round_trips_without_decoding()
    {
        var checkpoint = new string('A', 19246);
        var calls = 0;
        using var handler = new TerraBaseTestHandler(async (request, _) =>
        {
            calls++;
            await Assert.That(request.Headers.GetValues("Last-Event-ID").Single()).IsEqualTo(checkpoint);
            return Sse(new MemoryStream(Encoding.UTF8.GetBytes($"event: cursor\nid: {checkpoint}\ndata: {{\"cursor\":\"{checkpoint}\"}}\n\n")));
        });
        using var http = new HttpClient(handler);
        var events = new List<TerraBaseChangesEvent>();
        await foreach (var item in Database(http).StreamChangesAsync(lastEventId: checkpoint)) events.Add(item);
        await Assert.That(events.Count).IsEqualTo(1);
        await Assert.That(events[0].Cursor).IsEqualTo(checkpoint);
        await Assert.That(calls).IsEqualTo(1);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task A_checkpoint_over_the_producers_bound_is_rejected_in_either_direction(bool fromServer)
    {
        var checkpoint = new string('A', 19247);
        var calls = 0;
        using var handler = new TerraBaseTestHandler((_, _) =>
        {
            calls++;
            return Task.FromResult(Sse(new MemoryStream(Encoding.UTF8.GetBytes($"event: cursor\nid: {checkpoint}\ndata: {{\"cursor\":\"{checkpoint}\"}}\n\n"))));
        });
        using var http = new HttpClient(handler);
        var stream = Database(http).StreamChangesAsync(lastEventId: fromServer ? null : checkpoint);
        if (fromServer)
        {
            var error = await Assert.ThrowsAsync<TerraBaseException>(() => DrainAsync(stream));
            await Assert.That(error!.ErrorCode).IsEqualTo("invalid_response");
        }
        else await Assert.ThrowsAsync<ArgumentException>(() => DrainAsync(stream));
        await Assert.That(calls).IsEqualTo(fromServer ? 1 : 0);
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
    public async Task Deadline_expiring_while_stream_acquisition_is_disarmed_is_reported_as_timeout()
    {
        using var source = new WaitingStream();
        using var handler = new TerraBaseTestHandler((_, _) => Task.FromResult(Sse(source)));
        using var http = new HttpClient(handler);
        var connection = new TerraBaseConnection(http, new Uri("https://sjc.api.terrascale.tech"), null, "action-token", "ApiKey",
            TimeSpan.FromSeconds(30), TerraBaseConnection.DefaultMaxResponseBytes, timeProvider: new ExpireOnDisarmClock());
        var db = new TerraBaseDatabase(connection, $"/v1/projects/{TerraBaseIntegrationFixture.ProjectId}/databases/app");
        await Assert.ThrowsAsync<TimeoutException>(() => DrainAsync(db.StreamChangesAsync()));
        await Assert.That(source.Started.Task.IsCompleted).IsFalse();
        await Assert.That(source.Disposed).IsTrue();
    }

    [Test]
    public async Task An_expiry_callback_released_after_acquisition_cannot_cancel_the_stream()
    {
        using var source = new WaitingStream();
        using var handler = new TerraBaseTestHandler((_, _) => Task.FromResult(Sse(source)));
        using var http = new HttpClient(handler);
        using var cancellation = new CancellationTokenSource();
        var clock = new HeldExpiryClock();
        var connection = new TerraBaseConnection(http, new Uri("https://sjc.api.terrascale.tech"), null, "action-token", "ApiKey",
            TimeSpan.FromSeconds(30), TerraBaseConnection.DefaultMaxResponseBytes, timeProvider: clock);
        var db = new TerraBaseDatabase(connection, $"/v1/projects/{TerraBaseIntegrationFixture.ProjectId}/databases/app");
        var read = DrainAsync(db.StreamChangesAsync(cancellationToken: cancellation.Token));
        try
        {
            var token = await source.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.That(clock.Disarmed).IsTrue();
            clock.ReleaseCallback();
            await Assert.That(token.IsCancellationRequested).IsFalse();
            await Assert.That(read.IsCompleted).IsFalse();
        }
        finally
        {
            await cancellation.CancelAsync();
            await Assert.ThrowsAsync<OperationCanceledException>(() => read);
        }
        await Assert.That(source.Disposed).IsTrue();
    }

    [Test]
    [Arguments(false, true, true)]
    [Arguments(true, false, true)]
    [Arguments(true, true, true)]
    [Arguments(true, true, false)]
    public async Task Idle_deadline_ends_a_silent_stream_and_caller_cancellation_takes_precedence(bool callerCancels, bool idleExpires, bool idleFirst)
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var source = new WaitingStream(release.Task);
        using var content = new DisposalTrackedStreamContent(source);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/event-stream");
        using var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        using var handler = new TerraBaseTestHandler((_, _) => Task.FromResult(response));
        using var http = new HttpClient(handler);
        using var cancellation = new CancellationTokenSource();
        var clock = new HeldExpiryClock();
        var connection = new TerraBaseConnection(http, new Uri("https://sjc.api.terrascale.tech"), null, "action-token", "ApiKey",
            TimeSpan.FromSeconds(30), TerraBaseConnection.DefaultMaxResponseBytes, timeProvider: clock);
        var db = new TerraBaseDatabase(connection, $"/v1/projects/{TerraBaseIntegrationFixture.ProjectId}/databases/app");
        var read = DrainAsync(db.StreamChangesAsync(cancellationToken: cancellation.Token));
        try
        {
            await source.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.That(clock.Disarmed).IsTrue();
            await Assert.That(clock.HasIdleTimer).IsTrue();
            if (callerCancels && !idleFirst) await cancellation.CancelAsync();
            if (idleExpires)
            {
                clock.ReleaseIdleCallback();
                await source.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            }
            // Hold the read's cancellation exception until both signals are set, without scheduler races.
            if (callerCancels && idleFirst) await cancellation.CancelAsync();
            release.TrySetResult();
            if (callerCancels)
                await Assert.ThrowsAsync<OperationCanceledException>(() => read);
            else
            {
                var error = await Assert.ThrowsAsync<TimeoutException>(() => read);
                await Assert.That(error!.Message).Contains("stopped sending bytes for 45 seconds");
            }
            await Assert.That(content.Disposed).IsTrue();
            await Assert.That(source.Disposed).IsTrue();
        }
        finally
        {
            await cancellation.CancelAsync();
            release.TrySetResult();
            try { await read; }
            catch (Exception error) when (error is OperationCanceledException or TimeoutException) { }
        }
    }

    [Test]
    public async Task A_completed_reads_idle_callback_cannot_cancel_the_next_read()
    {
        using var source = new WaitingStream(prefix: Encoding.UTF8.GetBytes("event: cursor\nid: AQ\ndata: {\"cursor\":\"AQ\"}\n\n"));
        using var handler = new TerraBaseTestHandler((_, _) => Task.FromResult(Sse(source)));
        using var http = new HttpClient(handler);
        using var cancellation = new CancellationTokenSource();
        var clock = new HeldExpiryClock();
        var connection = new TerraBaseConnection(http, new Uri("https://sjc.api.terrascale.tech"), null, "action-token", "ApiKey",
            TimeSpan.FromSeconds(30), TerraBaseConnection.DefaultMaxResponseBytes, timeProvider: clock);
        var db = new TerraBaseDatabase(connection, $"/v1/projects/{TerraBaseIntegrationFixture.ProjectId}/databases/app");
        await using var enumerator = db.StreamChangesAsync(cancellationToken: cancellation.Token).GetAsyncEnumerator();
        await Assert.That(await enumerator.MoveNextAsync()).IsTrue();
        await Assert.That(enumerator.Current.Cursor).IsEqualTo("AQ");
        var read = enumerator.MoveNextAsync().AsTask();
        try
        {
            var token = await source.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.That(clock.IdleTimerCount).IsEqualTo(2);
            clock.ReleaseIdleCallback();
            await Assert.That(token.IsCancellationRequested).IsFalse();
            await Assert.That(read.IsCompleted).IsFalse();
        }
        finally
        {
            await cancellation.CancelAsync();
            await Assert.ThrowsAsync<OperationCanceledException>(() => read);
        }
        await Assert.That(source.Disposed).IsTrue();
    }

    [Test]
    [Arguments("event: cursor\nid: AQ\ndata: {\"cursor\":\"different\"}\n\n")]
    [Arguments("event: change\nid: AQ\ndata: {}\n\n")]
    [Arguments("event: change\ndata: {}\n\n")]
    [Arguments("event: cursor\nid: AQ\ndata: {\"cursor\":\"AQ\"}\n\nevent: cursor\ndata: {\"cursor\":\"AQ\"}\n\n")]
    [Arguments("event: change\nid:\ndata: {\"collection\":\"items\",\"id\":\"a\",\"op\":\"upsert\",\"version\":\"2\",\"origin\":\"0000000000000001\",\"document\":null}\n\n")]
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
    [Arguments(false)]
    [Arguments(true)]
    public async Task Routing_421_preserves_response_headers_without_following(bool structured)
    {
        var calls = 0;
        using var handler = new TerraBaseTestHandler((_, _) =>
        {
            calls++;
            var response = structured
                ? TerraBaseIntegrationFixture.Json("""{"error":{"code":"cursor_elsewhere","message":"the cursor resumes on its issuing machine"}}""", HttpStatusCode.MisdirectedRequest)
                : new HttpResponseMessage(HttpStatusCode.MisdirectedRequest);
            response.Headers.Add("X-TerraBase-Region", "gru");
            response.Headers.Add("ts-replay", "region=sjc");
            response.Headers.Add("Cache-Control", "no-store");
            return Task.FromResult(response);
        });
        using var http = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<TerraBaseWrongRegionException>(() => DrainAsync(Database(http).StreamChangesAsync()));
        await Assert.That(error!.Region).IsEqualTo("gru");
        await Assert.That(error.ErrorCode).IsEqualTo(structured ? "cursor_elsewhere" : null);
        await Assert.That(error.ResponseHeaders["TS-REPLAY"].Single()).IsEqualTo("region=sjc");
        await Assert.That(error.ResponseHeaders["Cache-Control"].Single()).IsEqualTo("no-store");
        await Assert.That(calls).IsEqualTo(1);
    }

    [Test]
    public async Task Empty_421_ignores_ts_replay_and_preserves_region_and_stream_scope_without_retry()
    {
        var sent = new List<HttpRequestMessage>();
        using var handler = new TerraBaseTestHandler((request, _) =>
        {
            sent.Add(request);
            var response = new HttpResponseMessage(HttpStatusCode.MisdirectedRequest);
            response.Headers.Add("X-TerraBase-Region", "gru");
            response.Headers.Add("ts-replay", "region=sjc"); // Producer #353's mismatched-pin response.
            response.Headers.Location = new Uri("https://gru.api.terrascale.test/");
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(1));
            return Task.FromResult(response);
        });
        using var http = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<TerraBaseWrongRegionException>(() =>
            DrainAsync(Database(http).StreamChangesAsync(lastEventId: "previous", collections: ["items"])));
        await Assert.That(error!.StatusCode).IsEqualTo(HttpStatusCode.MisdirectedRequest);
        await Assert.That(error.Region).IsEqualTo("gru");
        await Assert.That(error.ErrorCode).IsNull();
        await Assert.That(error.HomeRegion).IsNull();
        await Assert.That(error.Regions).IsEmpty();
        await Assert.That(error.RetryAfter).IsEqualTo(TimeSpan.FromSeconds(1));
        await Assert.That(sent.Count).IsEqualTo(1);
        await Assert.That(sent[0].Method).IsEqualTo(HttpMethod.Get);
        await Assert.That(sent[0].RequestUri).IsEqualTo(new Uri($"https://api.terrascale.tech/v1/projects/{TerraBaseIntegrationFixture.ProjectId}/databases/app/changes?collections=items"));
        await Assert.That(sent[0].Headers.GetValues("X-TerraScale-Region-Pin").Single()).IsEqualTo("sjc");
        await Assert.That(sent[0].Headers.GetValues("Last-Event-ID").Single()).IsEqualTo("previous");
        await Assert.That(sent[0].Headers.Authorization!.Scheme).IsEqualTo("Bearer");
        await Assert.That(sent[0].Headers.Authorization!.Parameter).IsEqualTo("project-user-token");
        await Assert.That(sent[0].Headers.Accept.Single().MediaType).IsEqualTo("text/event-stream");
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

    private sealed class WaitingStream(Task? releaseCancellation = null, byte[]? prefix = null) : MemoryStream(prefix ?? [])
    {
        public TaskCompletionSource<CancellationToken> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Disposed { get; private set; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Position < Length) return await base.ReadAsync(buffer, cancellationToken);
            Started.TrySetResult(cancellationToken);
            try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
            catch (OperationCanceledException) when (releaseCancellation is not null)
            {
                Cancelled.TrySetResult();
                await releaseCancellation;
                throw;
            }
            finally { Cancelled.TrySetResult(); }
            return 0;
        }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }

    private sealed class DisposalTrackedStreamContent(Stream source) : StreamContent(source)
    {
        public bool Disposed { get; private set; }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }

    private sealed class ExpireOnDisarmClock : TimeProvider
    {
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) => new ExpireOnDisarmTimer(callback, state);

        private sealed class ExpireOnDisarmTimer(TimerCallback callback, object? state) : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                // Simulate the deadline callback already running when acquisition tries to disable its timer.
                if (dueTime == Timeout.InfiniteTimeSpan) callback(state);
                return true;
            }
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class HeldExpiryClock : TimeProvider
    {
        private readonly Dictionary<TimeSpan, List<(TimerCallback Callback, object? State)>> callbacks = [];
        public bool Disarmed { get; private set; }
        public bool HasIdleTimer => callbacks.ContainsKey(TimeSpan.FromSeconds(45));
        public int IdleTimerCount => callbacks[TimeSpan.FromSeconds(45)].Count;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            if (!callbacks.TryGetValue(dueTime, out var timers)) callbacks[dueTime] = timers = [];
            timers.Add((callback, state));
            return new HeldTimer(() => { if (dueTime == TimeSpan.FromSeconds(30)) Disarmed = true; });
        }
        // Resume an expiry callback that was already queued before the timer was disarmed.
        public void ReleaseCallback() => Release(TimeSpan.FromSeconds(30));
        public void ReleaseIdleCallback() => Release(TimeSpan.FromSeconds(45));
        private void Release(TimeSpan dueTime)
        {
            var (callback, state) = callbacks[dueTime][0];
            callback(state);
        }

        private sealed class HeldTimer(Action disarmed) : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                if (dueTime == Timeout.InfiniteTimeSpan) disarmed();
                return true;
            }
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}

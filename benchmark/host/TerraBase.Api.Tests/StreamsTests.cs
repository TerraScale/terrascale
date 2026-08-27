// <copyright file="StreamsTests.cs" company="TerraScale">
// TerraBaseV2 (SE-08). See LICENSE.md.
// </copyright>

namespace TerraBase.Api.Tests;

using System.Net.WebSockets;
using System.Text.Json;

/// <summary>
/// The subscription-endpoint rows of the AC (against the scripted
/// engine): the SSE and WebSocket endpoints are two projections of the
/// same TBEP backbone subscription — parity of the delivered frames,
/// checkpoint/resume on reconnect (the explicit <c>from</c> replay and
/// the <c>Last-Event-ID</c> auto-resume), the bounded per-stream queue
/// with counted, surfaced drops, and the stream lifecycle (acknowledge,
/// close, unknown-subscription refusals).
/// </summary>
public sealed class StreamsTests
{
    private static string SocketPath() => Path.Combine(Path.GetTempPath(), $"se08-streams-{Guid.NewGuid():N}.sock");

    /// <summary>One write lands, with one idempotency key, as one journal commit.</summary>
    private static async Task<(int Status, JsonDocument Body)> WriteAsync(HttpClient caller, string document, string operation)
    {
        var answer = await HostHarness.SendAsync(
            caller,
            $"/v1/documents/orders/{document}",
            new { @string = $"body-of-{document}" },
            idempotencyKey: operation,
            method: "PUT");
        return answer;
    }

    /// <summary>SSE and WebSocket deliver the same backbone frames: the same batches, triggers, and sequences.</summary>
    [Test]
    public async Task SseAndWebSocketDeliverTheSameBackboneFrames()
    {
        var path = SocketPath();
        await using var engine = new ScriptedTbepEngine(path);
        await using var host = await HostHarness.StartAsync(new EngineOptions { SocketPath = path });
        var caller = host.As("backend-operator", "tenant-a");

        // Two commits land before either consumer connects.
        await WriteAsync(caller, "d-01", "op-01");
        await WriteAsync(caller, "d-02", "op-02");

        await using var sse = await SseReader.OpenAsync(caller, "collections=orders&from=1");
        using var ws = await Ws.OpenAsync(host.Http.BaseAddress!, "backend-operator", "tenant-a", "collections=orders&from=1");

        var sseOpened = await sse.ExpectAsync("opened");
        var wsOpened = await Ws.ExpectAsync(ws, "opened");
        await Assert.That(sseOpened.Data.RootElement.GetProperty("startSequence").GetUInt64()).IsEqualTo(3ul);
        await Assert.That(wsOpened.RootElement.GetProperty("startSequence").GetUInt64()).IsEqualTo(3ul);

        // The replayed journal (both commits) reaches both consumers.
        var sseReplay = await sse.ExpectAsync("batch");
        var wsReplay = await Ws.ExpectAsync(ws, "batch");
        var sseEvents = sseReplay.Data.RootElement.GetProperty("events").EnumerateArray().ToArray();
        var wsEvents = wsReplay.RootElement.GetProperty("events").EnumerateArray().ToArray();
        await Assert.That(sseEvents.Length).IsEqualTo(2);
        await Assert.That(wsEvents.Length).IsEqualTo(2);
        for (var index = 0; index < sseEvents.Length; index++)
        {
            await Assert.That(sseEvents[index].GetProperty("sequence").GetUInt64()).IsEqualTo(wsEvents[index].GetProperty("sequence").GetUInt64());
            await Assert.That(sseEvents[index].GetProperty("operationId").GetString()).IsEqualTo(wsEvents[index].GetProperty("operationId").GetString());
        }

        // A live commit reaches both consumers as the ordered batch and
        // the content-free trigger, in arrival order.
        await WriteAsync(caller, "d-03", "op-03");
        var sseLive = await sse.ExpectAsync("batch");
        var wsLive = await Ws.ExpectAsync(ws, "batch");
        await Assert.That(sseLive.Data.RootElement.GetProperty("events").EnumerateArray().Single().GetProperty("sequence").GetUInt64()).IsEqualTo(3ul);
        await Assert.That(wsLive.RootElement.GetProperty("events").EnumerateArray().Single().GetProperty("sequence").GetUInt64()).IsEqualTo(3ul);
        var sseTrigger = await sse.ExpectAsync("trigger");
        var wsTrigger = await Ws.ExpectAsync(ws, "trigger");
        await Assert.That(sseTrigger.Data.RootElement.GetProperty("sequence").GetUInt64()).IsEqualTo(3ul);
        await Assert.That(wsTrigger.RootElement.GetProperty("sequence").GetUInt64()).IsEqualTo(3ul);

        // The trigger is content-free (PD-2026-08-26-13 D3): the hint
        // carries the subscription, collection, sequence, and operation
        // id — never a document body.
        await Assert.That(sseTrigger.Data.RootElement.GetProperty("collection").GetString()).IsEqualTo("orders");
        await Assert.That(sseTrigger.Data.RootElement.GetProperty("operationId").GetString()).IsEqualTo("op-03");
        var triggerText = sseTrigger.Data.RootElement.GetRawText();
        await Assert.That(triggerText.Contains("body-of-d-03", StringComparison.Ordinal)).IsFalse();
    }

    /// <summary>A collection filter scopes the stream: other collections never arrive.</summary>
    [Test]
    public async Task CollectionFiltersScopeTheStream()
    {
        var path = SocketPath();
        await using var engine = new ScriptedTbepEngine(path);
        await using var host = await HostHarness.StartAsync(new EngineOptions { SocketPath = path });
        var caller = host.As("backend-operator", "tenant-a");
        await WriteAsync(caller, "d-01", "op-01");

        await using var sse = await SseReader.OpenAsync(caller, "collections=users&from=1");
        var opened = await sse.ExpectAsync("opened");
        await Assert.That(opened.Data.RootElement.GetProperty("collections").EnumerateArray().Single().GetString()).IsEqualTo("users");
        await WriteAsync(caller, "d-02", "op-02");
        await WriteAsync(caller, "d-03", "op-03");

        // No batch, no trigger: only the filtered collections arrive.
        using var quiet = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var next = await sse.ReadAsync(quiet.Token);
        await Assert.That(next).IsNull();
    }

    /// <summary>The collection filter validates: a bad scope is refused before the engine.</summary>
    [Test]
    public async Task CollectionFiltersValidate()
    {
        var path = SocketPath();
        await using var engine = new ScriptedTbepEngine(path);
        await using var host = await HostHarness.StartAsync(new EngineOptions { SocketPath = path });
        var caller = host.As("backend-operator", "tenant-a");

        using var refused = await caller.GetAsync("/v1/streams?collections=orders,bad%20scope", HttpCompletionOption.ResponseHeadersRead);
        await Assert.That((int)refused.StatusCode).IsEqualTo(422);
        refused.Dispose();
    }

    /// <summary>An explicit from-sequence resumes the journal at that checkpoint.</summary>
    [Test]
    public async Task AnExplicitFromSequenceReplaysFromTheCheckpoint()
    {
        var path = SocketPath();
        await using var engine = new ScriptedTbepEngine(path);
        await using var host = await HostHarness.StartAsync(new EngineOptions { SocketPath = path });
        var caller = host.As("backend-operator", "tenant-a");
        for (var index = 1; index <= 3; index++)
        {
            await WriteAsync(caller, $"d-0{index}", $"op-0{index}");
        }

        await using var sse = await SseReader.OpenAsync(caller, "from=2");
        var replay = await sse.ExpectAsync("batch");
        var sequences = replay.Data.RootElement.GetProperty("events").EnumerateArray()
            .Select(@event => @event.GetProperty("sequence").GetUInt64())
            .ToArray();
        await Assert.That(sequences).IsEquivalentTo([2ul, 3ul]);
        await Assert.That(replay.Id).IsEqualTo("3");
    }

    /// <summary>An SSE reconnect with Last-Event-ID resumes strictly after the last seen event — no duplicates.</summary>
    [Test]
    public async Task ALastEventIdReconnectResumesStrictlyAfter()
    {
        var path = SocketPath();
        await using var engine = new ScriptedTbepEngine(path);
        await using var host = await HostHarness.StartAsync(new EngineOptions { SocketPath = path });
        var caller = host.As("backend-operator", "tenant-a");
        for (var index = 1; index <= 3; index++)
        {
            await WriteAsync(caller, $"d-0{index}", $"op-0{index}");
        }

        // The first consumer sees the replay through sequence 2.
        await using (var first = await SseReader.OpenAsync(caller, "from=1"))
        {
            var batch = await first.ExpectAsync("batch");
            var last = batch.Data.RootElement.GetProperty("events").EnumerateArray().Last().GetProperty("sequence").GetUInt64();
            await Assert.That(last).IsEqualTo(3ul);
        }

        // The consumer disconnects after sequence 2 (its stored id);
        // one further commit lands while it is away.
        await WriteAsync(caller, "d-04", "op-04");

        // The reconnect with Last-Event-ID: 2 replays exactly the missed
        // events (3 and 4) — the sequence-2 event is not re-delivered.
        await using var second = await SseReader.OpenAsync(caller, string.Empty, lastEventId: "2");
        var resumed = await second.ExpectAsync("batch");
        var sequences = resumed.Data.RootElement.GetProperty("events").EnumerateArray()
            .Select(@event => @event.GetProperty("sequence").GetUInt64())
            .ToArray();
        await Assert.That(sequences).IsEquivalentTo([3ul, 4ul]);
    }

    /// <summary>A full bounded queue drops the oldest frame, counts the drop, and surfaces it in-band and in the metrics.</summary>
    [Test]
    public async Task AFullBoundedQueueDropsCountsAndSurfaces()
    {
        var path = SocketPath();
        await using var engine = new ScriptedTbepEngine(path);
        await using var host = await HostHarness.StartAsync(new EngineOptions { SocketPath = path });
        var caller = host.As("backend-operator", "tenant-a");

        // The hub opens the subscription; nothing consumes it while the
        // journal runs ahead — every broadcast offers the stream's
        // bounded queue (depth 64) a batch and a trigger.
        var stream = await host.Hub.OpenAsync("tenant-a", "backend-operator", null, fromSequence: 1);
        for (var index = 0; index < 80; index++)
        {
            var (status, _) = await WriteAsync(caller, $"d-{index:D3}", $"op-{index:D3}");
            await Assert.That(status).IsEqualTo(200);
        }

        await Assert.That(stream.Dropped).IsGreaterThan(0);
        await Assert.That(host.Hub.DroppedTotal).IsEqualTo(stream.Dropped);

        // The drop is surfaced in-band: the consumer reads drop markers
        // carrying the sequence it must resume from, after the queue
        // fell a full depth behind.
        var kinds = new List<StreamEvent>();
        while (await stream.ReadAsync(TimeSpan.FromSeconds(5)) is { } frame)
        {
            kinds.Add(frame);
        }

        await Assert.That(kinds.Count(item => item is StreamEvent.Dropped)).IsGreaterThan(0);
        var drop = kinds.OfType<StreamEvent.Dropped>().First();
        await Assert.That(drop.ResumeFrom).IsGreaterThan(0ul);

        // The count is surfaced in the host metrics and audited.
        var (metricsStatus, metrics) = await HostHarness.SendAsync(host.Http, "/v1/metrics");
        await Assert.That(metricsStatus).IsEqualTo(200);
        await Assert.That(metrics.RootElement.GetProperty("droppedFrames").GetInt64()).IsEqualTo(stream.Dropped);
        await Assert.That(metrics.RootElement.GetProperty("openStreams").GetInt32()).IsEqualTo(1);
        await Assert.That(host.Audit.Recent(AuditLog.Capacity).Count(record => record.Kind == AuditKind.StreamDropped)).IsGreaterThan(0);
        await host.Hub.CloseAsync(stream, reason: "test-complete").ConfigureAwait(false);
    }

    /// <summary>Acknowledging a stream records the consumer's checkpoint with the engine.</summary>
    [Test]
    public async Task AcknowledgingAStreamRecordsTheCheckpoint()
    {
        var path = SocketPath();
        await using var engine = new ScriptedTbepEngine(path);
        await using var host = await HostHarness.StartAsync(new EngineOptions { SocketPath = path });
        var caller = host.As("backend-operator", "tenant-a");
        await WriteAsync(caller, "d-01", "op-01");

        await using var sse = await SseReader.OpenAsync(caller, "from=1");
        var opened = await sse.ExpectAsync("opened");
        var subscriptionId = opened.Data.RootElement.GetProperty("subscriptionId").GetUInt64();
        await sse.ExpectAsync("batch");

        var (status, body) = await HostHarness.SendAsync(caller, $"/v1/streams/{subscriptionId}/ack", new { throughSequence = 1 });
        await Assert.That(status).IsEqualTo(200);
        await Assert.That(body.RootElement.GetProperty("subscriptionId").GetUInt64()).IsEqualTo(subscriptionId);
        await Assert.That(body.RootElement.GetProperty("acknowledgedThrough").GetUInt64()).IsEqualTo(1ul);
    }

    /// <summary>The stream lifecycle after close: an unknown subscription is refused as ended.</summary>
    [Test]
    public async Task ALifecycleFrameOnAClosedSubscriptionIsRefusedAsEnded()
    {
        var path = SocketPath();
        await using var engine = new ScriptedTbepEngine(path);
        await using var host = await HostHarness.StartAsync(new EngineOptions { SocketPath = path });
        var caller = host.As("backend-operator", "tenant-a");
        await WriteAsync(caller, "d-01", "op-01");

        await using var sse = await SseReader.OpenAsync(caller, "from=1");
        var opened = await sse.ExpectAsync("opened");
        var subscriptionId = opened.Data.RootElement.GetProperty("subscriptionId").GetUInt64();
        await sse.ExpectAsync("batch");

        // The consumer closes its stream: the engine unsubscribes and
        // the close frame reaches the consumer.
        var (closed, _) = await HostHarness.SendAsync(caller, $"/v1/streams/{subscriptionId}", method: "DELETE");
        await Assert.That(closed).IsEqualTo(200);
        var closeEvent = await sse.ExpectAsync("closed");
        await Assert.That(closeEvent.Data.RootElement.GetProperty("reason").GetString()).IsEqualTo("replaced");

        // A lifecycle frame naming the closed subscription is refused
        // as ended — truthfully, without a tenant-refused audit.
        var (ackStatus, ackBody) = await HostHarness.SendAsync(caller, $"/v1/streams/{subscriptionId}/ack", new { throughSequence = 1 });
        await Assert.That(ackStatus).IsEqualTo(410);
        await Assert.That(ackBody.RootElement.GetProperty("code").GetString()).IsEqualTo("subscription-ended");
        await Assert.That(host.Audit.Recent(16).Any(record => record.Kind == AuditKind.TenantRefused)).IsFalse();
    }

    /// <summary>A non-WebSocket request to the WebSocket endpoint is refused plainly.</summary>
    [Test]
    public async Task APlainGetOnTheWebSocketEndpointIsRefused()
    {
        var path = SocketPath();
        await using var engine = new ScriptedTbepEngine(path);
        await using var host = await HostHarness.StartAsync(new EngineOptions { SocketPath = path });
        var caller = host.As("backend-operator", "tenant-a");
        var (status, _) = await HostHarness.SendAsync(caller, "/v1/streams/ws");
        await Assert.That(status).IsEqualTo(400);
    }
}

/// <summary>The stream-queue reader half the drop row needs: drains with a bound.</summary>
file static class BackboneStreamReading
{
    /// <summary>Reads the next frame of the stream with a timeout bound.</summary>
    /// <param name="stream">The stream.</param>
    /// <param name="within">The bound.</param>
    /// <returns>The frame, or <c>null</c> when the stream ended or the bound elapsed.</returns>
    public static async Task<StreamEvent?> ReadAsync(this BackboneStream stream, TimeSpan within)
    {
        using var bound = new CancellationTokenSource(within);
        try
        {
            return await stream.ReadAsync(bound.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }
}

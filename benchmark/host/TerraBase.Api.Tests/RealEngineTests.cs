// <copyright file="RealEngineTests.cs" company="TerraScale">
// TerraBaseV2 (SE-08). See LICENSE.md.
// </copyright>

namespace TerraBase.Api.Tests;

/// <summary>
/// The real-engine end-to-end rows of the AC: the repository's actual
/// TBEP engine server — <c>TbepServer</c> over <c>SyncDeliveryHost</c>
/// from <c>crates/terrabase-tbep</c>, built into a throwaway cargo
/// project outside the repository exactly as the crate's own UDS
/// harness consumes it — spawned, watched, and served by the host's
/// supervisor over a real Unix-domain socket. The rows cover the real
/// channel binding and engine-enforced tenant isolation, the real sync
/// sessions (Loro deltas, idempotent retries, the retained-floor
/// resync), the real backbone streams with content-free triggers, the
/// topology/admin/supervision reads, and the graceful-shutdown
/// contract. The document/query/family operations answer the engine's
/// truthful typed refusal (the engine-backed host is the named
/// integrator action of the SE-14 record, not this packet).
/// </summary>
public sealed class RealEngineTests
{
    private static async Task<EngineOptions> OptionsAsync()
    {
        var binary = await EngineHarness.BinaryPathAsync();
        return new EngineOptions
        {
            SocketPath = Path.Combine(Path.GetTempPath(), $"se08-real-{Guid.NewGuid():N}.sock"),
            ManagedBinary = binary,
            MaxRestarts = 1,
        };
    }

    private static async Task<(HostHarness Host, HttpClient Caller)> StartAsync(string tenant = "tenant-a")
    {
        var host = await HostHarness.StartAsync(await OptionsAsync());
        return (host, host.As("backend-operator", tenant));
    }

    /// <summary>The host serves a healthy engine generation: truthful health, real counters.</summary>
    [Test]
    public async Task TheHostServesAHealthyEngineGeneration()
    {
        var (host, caller) = await StartAsync();
        await using var _ = host;

        var (health, healthBody) = await HostHarness.SendAsync(host.Http, "/health");
        await Assert.That(health).IsEqualTo(200);
        await Assert.That(healthBody.RootElement.GetProperty("status").GetString()).IsEqualTo("serving");
        await Assert.That(healthBody.RootElement.GetProperty("engine").GetProperty("state").GetString()).IsEqualTo("Serving");
        await Assert.That(healthBody.RootElement.GetProperty("engine").GetProperty("generation").GetInt32()).IsEqualTo(1);

        var (engineHealth, engineBody) = await HostHarness.SendAsync(caller, "/v1/engine/health");
        await Assert.That(engineHealth).IsEqualTo(200);
        await Assert.That(engineBody.RootElement.GetProperty("engineState").GetString()).IsEqualTo("Serving");

        var (topology, topologyBody) = await HostHarness.SendAsync(caller, "/v1/engine/topology");
        await Assert.That(topology).IsEqualTo(200);
        await Assert.That(topologyBody.RootElement.GetProperty("binding").GetProperty("variant").GetString()).IsEqualTo("LocalUds");

        var (status, statusBody) = await HostHarness.SendAsync(caller, "/v1/engine/status");
        await Assert.That(status).IsEqualTo(200);
        await Assert.That(statusBody.RootElement.GetProperty("registryVersion").GetString()).IsEqualTo(TbepRegistry.RegistryVersion);
    }

    /// <summary>The engine enforces tenant isolation on real sessions: a cross-tenant lifecycle frame is refused and audited.</summary>
    [Test]
    public async Task TheEngineEnforcesTenantIsolationOnRealSessions()
    {
        var (host, owner) = await StartAsync();
        await using var _ = host;
        var intruder = host.As("backend-operator", "tenant-b");

        var (setup, setupBody) = await HostHarness.SendAsync(owner, "/v1/sync/sessions", new { collection = "orders" });
        await Assert.That(setup).IsEqualTo(200);
        var sessionId = setupBody.RootElement.GetProperty("sessionId").GetUInt64();
        var checkpoint = setupBody.RootElement.GetProperty("checkpoint").GetString()!;

        // The intruder's channel names the owner's session: refused,
        // audited, and the owner keeps full ownership.
        var (intruderDelta, intruderBody) = await HostHarness.SendAsync(intruder, $"/v1/sync/sessions/{sessionId}/delta", new { from = checkpoint });
        await Assert.That(intruderDelta).IsEqualTo(403);
        await Assert.That(intruderBody.RootElement.GetProperty("code").GetString()).IsEqualTo("tenant-refused");
        var (intruderClose, _) = await HostHarness.SendAsync(intruder, $"/v1/sync/sessions/{sessionId}/close", method: "POST");
        await Assert.That(intruderClose).IsEqualTo(403);
        await Assert.That(host.Audit.Recent(16).Count(record => record.Kind == AuditKind.TenantRefused)).IsEqualTo(2);

        // The owner's own session still serves and closes cleanly.
        var (ownerDelta, _) = await HostHarness.SendAsync(owner, $"/v1/sync/sessions/{sessionId}/delta", new { from = checkpoint });
        await Assert.That(ownerDelta).IsEqualTo(200);
        var (ownerClose, _) = await HostHarness.SendAsync(owner, $"/v1/sync/sessions/{sessionId}/close", method: "POST");
        await Assert.That(ownerClose).IsEqualTo(200);
    }

    /// <summary>The engine-backed document/query/family operations answer the truthful typed refusal.</summary>
    [Test]
    public async Task TheEngineBackedOperationsAnswerTheTypedRefusal()
    {
        var (host, caller) = await StartAsync();
        await using var _ = host;

        var (read, readBody) = await HostHarness.SendAsync(caller, "/v1/documents/orders/d-01");
        await Assert.That(read).IsEqualTo(501);
        await Assert.That(readBody.RootElement.GetProperty("code").GetString()).IsEqualTo("operation_refused");

        var (write, _) = await HostHarness.SendAsync(caller, "/v1/documents/orders/d-01", new { @string = "body" }, idempotencyKey: "op-01", method: "PUT");
        await Assert.That(write).IsEqualTo(501);
        var (query, _) = await HostHarness.SendAsync(caller, "/v1/query", new { text = "from orders" });
        await Assert.That(query).IsEqualTo(501);
        var (cancel, _) = await HostHarness.SendAsync(caller, "/v1/query/cancel", new { queryId = 1ul });
        await Assert.That(cancel).IsEqualTo(501);
        var (family, _) = await HostHarness.SendAsync(caller, "/v1/families/counter/total/read", new { requestCodec = "counter/1", request = Convert.ToBase64String([1, 2, 3]) });
        await Assert.That(family).IsEqualTo(501);
    }

    /// <summary>
    /// An engine decode refusal surfaces to the refused caller as its typed
    /// error — never as a hang. The registry's family kinds are a closed
    /// set; a family frame naming one outside it is refused at decode, and
    /// the engine's error frame names no causation (the frame's identity is
    /// unreadable by construction) — the channel routes it to the refused
    /// call.
    /// </summary>
    [Test]
    public async Task AnEngineDecodeRefusalSurfacesToTheRefusedCaller()
    {
        var (host, caller) = await StartAsync();
        await using var _ = host;

        var (refused, refusedBody) = await HostHarness.SendAsync(caller, "/v1/families/not-a-family-kind/total/read", new { requestCodec = "counter/1", request = Convert.ToBase64String([1, 2, 3]) });
        await Assert.That(refused).IsEqualTo(400);
        await Assert.That(refusedBody.RootElement.GetProperty("code").GetString()).IsEqualTo("malformed_frame");

        // The connection stayed usable: the next call answers normally.
        var (after, afterBody) = await HostHarness.SendAsync(caller, "/v1/documents/orders/d-01");
        await Assert.That(after).IsEqualTo(501);
        await Assert.That(afterBody.RootElement.GetProperty("code").GetString()).IsEqualTo("operation_refused");
    }

    /// <summary>Real sync sessions serve the delivery family end to end: mutations, deltas, snapshots, idempotent retries.</summary>
    [Test]
    public async Task RealSyncSessionsServeTheDeliveryFamily()
    {
        var (host, caller) = await StartAsync();
        await using var _ = host;

        var (setup, setupBody) = await HostHarness.SendAsync(caller, "/v1/sync/sessions", new { collection = "orders" });
        await Assert.That(setup).IsEqualTo(200);
        var sessionId = setupBody.RootElement.GetProperty("sessionId").GetUInt64();
        var checkpoint = setupBody.RootElement.GetProperty("checkpoint").GetString()!;
        await Assert.That(setupBody.RootElement.GetProperty("retainedFloor").GetString()).IsNotNull();

        // A fresh checkpoint is up to date.
        var (fresh, freshBody) = await HostHarness.SendAsync(caller, $"/v1/sync/sessions/{sessionId}/delta", new { from = checkpoint });
        await Assert.That(fresh).IsEqualTo(200);
        await Assert.That(freshBody.RootElement.GetProperty("commits").GetUInt32()).IsEqualTo(0u);

        // One server-ack mutation applies with a receipt over the real
        // sync core; the same key retried answers the recorded receipt.
        var mutation = new
        {
            document = "d-01",
            change = new { put = new[] { new { name = "qty", value = new { integer = 3L } } } },
        };
        var (applied, appliedBody) = await HostHarness.SendAsync(caller, $"/v1/sync/sessions/{sessionId}/mutations", mutation, idempotencyKey: "op-01");
        await Assert.That(applied).IsEqualTo(200);
        var receipt = appliedBody.RootElement.GetProperty("outcome").GetProperty("receipt");
        var sequence = receipt.GetProperty("sequence").GetUInt64();
        await Assert.That(sequence).IsGreaterThan(0ul);

        var (retry, retryBody) = await HostHarness.SendAsync(caller, $"/v1/sync/sessions/{sessionId}/mutations", mutation, idempotencyKey: "op-01");
        await Assert.That(retry).IsEqualTo(200);
        await Assert.That(retryBody.RootElement.GetProperty("outcome").GetProperty("receipt").GetProperty("sequence").GetUInt64()).IsEqualTo(sequence);

        // A distinct key advances the journal by exactly one: the retry
        // above did not commit twice.
        var (second, secondBody) = await HostHarness.SendAsync(caller, $"/v1/sync/sessions/{sessionId}/mutations", mutation, idempotencyKey: "op-02");
        await Assert.That(second).IsEqualTo(200);
        await Assert.That(secondBody.RootElement.GetProperty("outcome").GetProperty("receipt").GetProperty("sequence").GetUInt64()).IsEqualTo(sequence + 1);

        // The delta since the original checkpoint carries the missed
        // state (the core folds adjacent commits into boundaries; the
        // exact fold is the core's, pinned by the SE-04 vectors) and a
        // next checkpoint that serves up-to-date afterwards.
        var (delta, deltaBody) = await HostHarness.SendAsync(caller, $"/v1/sync/sessions/{sessionId}/delta", new { from = checkpoint });
        await Assert.That(delta).IsEqualTo(200);
        await Assert.That(deltaBody.RootElement.GetProperty("commits").GetUInt32()).IsGreaterThanOrEqualTo(1u);
        var next = deltaBody.RootElement.GetProperty("next").GetString()!;
        var (settled, settledBody) = await HostHarness.SendAsync(caller, $"/v1/sync/sessions/{sessionId}/delta", new { from = next });
        await Assert.That(settled).IsEqualTo(200);
        await Assert.That(settledBody.RootElement.GetProperty("commits").GetUInt32()).IsEqualTo(0u);
        var (ack, _) = await HostHarness.SendAsync(caller, $"/v1/sync/sessions/{sessionId}/ack", new { checkpoint = next });
        await Assert.That(ack).IsEqualTo(200);

        // The snapshot pull answers a real Loro snapshot.
        var (snapshot, snapshotBody) = await HostHarness.SendAsync(caller, $"/v1/sync/sessions/{sessionId}/snapshot", body: new { }, method: "POST");
        await Assert.That(snapshot).IsEqualTo(200);
        await Assert.That(snapshotBody.RootElement.GetProperty("payloadCodec").GetString()).IsEqualTo("loro-snapshot/1");
        await Assert.That(Convert.FromBase64String(snapshotBody.RootElement.GetProperty("payload").GetString()!)).IsNotEmpty();

        var (close, _) = await HostHarness.SendAsync(caller, $"/v1/sync/sessions/{sessionId}/close", method: "POST");
        await Assert.That(close).IsEqualTo(200);
    }

    /// <summary>A checkpoint behind the real retained floor is refused with the resync marker; the snapshot resyncs.</summary>
    [Test]
    public async Task ACheckpointBehindTheRetainedFloorResyncs()
    {
        var (host, caller) = await StartAsync();
        await using var _ = host;

        var (setup, setupBody) = await HostHarness.SendAsync(caller, "/v1/sync/sessions", new { collection = "orders" });
        await Assert.That(setup).IsEqualTo(200);
        var sessionId = setupBody.RootElement.GetProperty("sessionId").GetUInt64();
        var original = setupBody.RootElement.GetProperty("checkpoint").GetString()!;

        // Run the journal a full checkpoint window (128) ahead: the
        // original checkpoint falls behind the engine's retained floor.
        for (var index = 0; index < 132; index++)
        {
            var (status, _) = await HostHarness.SendAsync(
                caller,
                $"/v1/sync/sessions/{sessionId}/mutations",
                new
                {
                    document = $"d-{index % 8:D2}",
                    change = new { put = new[] { new { name = "n", value = new { integer = (long)index } } } },
                },
                idempotencyKey: $"op-{index:D3}");
            await Assert.That(status).IsEqualTo(200);
        }

        var (refused, refusedBody) = await HostHarness.SendAsync(caller, $"/v1/sync/sessions/{sessionId}/delta", new { from = original });
        await Assert.That(refused).IsEqualTo(409);
        await Assert.That(refusedBody.RootElement.GetProperty("code").GetString()).IsEqualTo("checkpoint-refused");
        await Assert.That(refusedBody.RootElement.GetProperty("resyncRequired").GetBoolean()).IsTrue();

        // The resync path: a fresh snapshot answers the full state with
        // a checkpoint the delta serves from.
        var (snapshot, snapshotBody) = await HostHarness.SendAsync(caller, $"/v1/sync/sessions/{sessionId}/snapshot", body: new { }, method: "POST");
        await Assert.That(snapshot).IsEqualTo(200);
        var fresh = snapshotBody.RootElement.GetProperty("checkpoint").GetString()!;
        var (resumed, resumedBody) = await HostHarness.SendAsync(caller, $"/v1/sync/sessions/{sessionId}/delta", new { from = fresh });
        await Assert.That(resumed).IsEqualTo(200);
        await Assert.That(resumedBody.RootElement.GetProperty("commits").GetUInt32()).IsEqualTo(0u);
    }

    /// <summary>Real backbone streams replay, follow live commits, and deliver content-free triggers.</summary>
    [Test]
    public async Task RealBackboneStreamsReplayFollowAndTrigger()
    {
        var (host, caller) = await StartAsync();
        await using var _ = host;

        // Two commits land, then the consumer subscribes from the start.
        var (setup, setupBody) = await HostHarness.SendAsync(caller, "/v1/sync/sessions", new { collection = "orders" });
        await Assert.That(setup).IsEqualTo(200);
        var sessionId = setupBody.RootElement.GetProperty("sessionId").GetUInt64();
        foreach (var operation in new[] { "op-01", "op-02" })
        {
            var (status, _) = await HostHarness.SendAsync(
                caller,
                $"/v1/sync/sessions/{sessionId}/mutations",
                new { document = "d-01", change = new { put = new[] { new { name = "n", value = new { @string = operation } } } } },
                idempotencyKey: operation);
            await Assert.That(status).IsEqualTo(200);
        }

        await using var sse = await SseReader.OpenAsync(caller, "collections=orders&from=1");
        var opened = await sse.ExpectAsync("opened");
        await Assert.That(opened.Data.RootElement.GetProperty("startSequence").GetUInt64()).IsGreaterThan(1ul);

        // The retained window replays through the subscription cursor.
        var replay = await sse.ExpectAsync("batch");
        var replayEvents = replay.Data.RootElement.GetProperty("events").EnumerateArray().ToArray();
        await Assert.That(replayEvents.Length).IsGreaterThanOrEqualTo(2);

        // A live commit arrives as the ordered batch plus the
        // content-free trigger, and the trigger carries no body.
        var (live, _) = await HostHarness.SendAsync(
            caller,
            $"/v1/sync/sessions/{sessionId}/mutations",
            new { document = "d-01", change = new { put = new[] { new { name = "n", value = new { @string = "live" } } } } },
            idempotencyKey: "op-live");
        await Assert.That(live).IsEqualTo(200);
        var batch = await sse.ExpectAsync("batch");
        await Assert.That(batch.Data.RootElement.GetProperty("events").EnumerateArray().Last().GetProperty("operationId").GetString()).IsEqualTo("op-live");
        var trigger = await sse.ExpectAsync("trigger");
        await Assert.That(trigger.Data.RootElement.GetProperty("operationId").GetString()).IsEqualTo("op-live");
        var triggerText = trigger.Data.RootElement.GetRawText();
        await Assert.That(triggerText.Contains("\"live\"", StringComparison.Ordinal)).IsFalse();
        await Assert.That(triggerText.Contains("qty", StringComparison.Ordinal)).IsFalse();

        // The acknowledgement records the consumer's checkpoint with
        // the real engine.
        var subscriptionId = opened.Data.RootElement.GetProperty("subscriptionId").GetUInt64();
        var through = batch.Data.RootElement.GetProperty("nextSequence").GetUInt64() - 1;
        var (ack, ackBody) = await HostHarness.SendAsync(caller, $"/v1/streams/{subscriptionId}/ack", new { throughSequence = through });
        await Assert.That(ack).IsEqualTo(200);
        await Assert.That(ackBody.RootElement.GetProperty("acknowledgedThrough").GetUInt64()).IsEqualTo(through);

        // An acknowledgement past what was served is refused.
        var (beyond, _) = await HostHarness.SendAsync(caller, $"/v1/streams/{subscriptionId}/ack", new { throughSequence = through + 100 });
        await Assert.That(beyond).IsEqualTo(410);
    }

    /// <summary>The graceful-shutdown contract: draining answer, streams closed, truthful health.</summary>
    [Test]
    public async Task TheGracefulShutdownContractHolds()
    {
        var (host, caller) = await StartAsync();
        await using var _ = host;

        await using var sse = await SseReader.OpenAsync(caller, "collections=orders");
        await sse.ExpectAsync("opened");

        var (shutdown, shutdownBody) = await HostHarness.SendAsync(caller, "/v1/engine/shutdown", new { reason = "operator" });
        await Assert.That(shutdown).IsEqualTo(200);
        await Assert.That(shutdownBody.RootElement.GetProperty("state").GetString()).IsEqualTo("Draining");

        // Every open subscription receives its close frame with the
        // shutting-down reason.
        var closed = await sse.ExpectAsync("closed");
        await Assert.That(closed.Data.RootElement.GetProperty("reason").GetString()).IsEqualTo("shutting_down");

        // The host's health is truthful: the generation ended
        // deliberately, and no restart is attempted.
        var (health, healthBody) = await HostHarness.SendAsync(host.Http, "/health");
        await Assert.That(health).IsEqualTo(503);
        await Assert.That(healthBody.RootElement.GetProperty("status").GetString()).IsEqualTo("unavailable");
        await Assert.That(host.Supervisor.Restarts).IsEqualTo(0);
        await Assert.That(host.Audit.Recent(16).Any(record => record.Kind == AuditKind.EngineShutdown)).IsTrue();
    }
}

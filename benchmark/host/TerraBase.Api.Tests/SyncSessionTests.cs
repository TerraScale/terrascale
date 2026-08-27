// <copyright file="SyncSessionTests.cs" company="TerraScale">
// TerraBaseV2 (SE-08). See LICENSE.md.
// </copyright>

namespace TerraBase.Api.Tests;

/// <summary>
/// The terrascale.sync.v2 session-forwarding rows of the AC (against
/// the scripted engine): the host terminates a client's session and
/// translates it onto the TBEP sync-delivery family — the full session
/// lifecycle (setup, delta, snapshot, checkpoint ack, server-ack
/// mutation, close), the at-least-once retry identity (a retried key
/// answers the recorded receipt, PD-2026-08-26-12 D2), the scalar
/// serving domain (composite field values are typed rejections, never
/// coerced), the fail-closed checkpoint refusals, and the ended-session
/// discipline (a session this host does not track is answered as ended,
/// never re-served).
/// </summary>
public sealed class SyncSessionTests
{
    private static string SocketPath() => Path.Combine(Path.GetTempPath(), $"se08-sync-{Guid.NewGuid():N}.sock");

    /// <summary>Opens one fresh session and answers its id and checkpoint.</summary>
    private static async Task<(ulong SessionId, byte[] Checkpoint)> OpenSessionAsync(HttpClient caller, string collection = "orders")
    {
        var (status, body) = await HostHarness.SendAsync(caller, "/v1/sync/sessions", new { collection });
        if (status != 200)
        {
            throw new InvalidOperationException($"the session did not open: {status} {body.RootElement.GetRawText()}");
        }

        return (body.RootElement.GetProperty("sessionId").GetUInt64(), Convert.FromBase64String(body.RootElement.GetProperty("checkpoint").GetString()!));
    }

    /// <summary>The full session lifecycle, forwarded end to end.</summary>
    [Test]
    public async Task TheSessionLifecycleRunsEndToEnd()
    {
        var path = SocketPath();
        await using var engine = new ScriptedTbepEngine(path);
        await using var host = await HostHarness.StartAsync(new EngineOptions { SocketPath = path });
        var caller = host.As("backend-operator", "tenant-a");

        var (sessionId, checkpoint) = await OpenSessionAsync(caller);
        await Assert.That(sessionId).IsGreaterThan(0ul);
        await Assert.That(checkpoint).IsNotEmpty();

        // A fresh checkpoint is up to date: the delta answers zero commits.
        var (fresh, freshBody) = await HostHarness.SendAsync(
            caller,
            $"/v1/sync/sessions/{sessionId}/delta",
            new { from = Convert.ToBase64String(checkpoint) });
        await Assert.That(fresh).IsEqualTo(200);
        await Assert.That(freshBody.RootElement.GetProperty("commits").GetUInt32()).IsEqualTo(0u);
        var next = Convert.FromBase64String(freshBody.RootElement.GetProperty("next").GetString()!);

        // One server-ack mutation is applied with its receipt.
        var (mutation, mutationBody) = await HostHarness.SendAsync(
            caller,
            $"/v1/sync/sessions/{sessionId}/mutations",
            new { document = "d-01", change = new { put = new[] { new { name = "qty", value = new { integer = 3L } } } } },
            idempotencyKey: "op-01");
        await Assert.That(mutation).IsEqualTo(200);
        await Assert.That(mutationBody.RootElement.GetProperty("outcome").GetProperty("outcome").GetString()).IsEqualTo("applied");
        var receipt = mutationBody.RootElement.GetProperty("outcome").GetProperty("receipt");
        await Assert.That(receipt.GetProperty("operationId").GetString()).IsEqualTo("op-01");

        // The checkpoint acknowledgement records the durable checkpoint.
        var (ack, _) = await HostHarness.SendAsync(caller, $"/v1/sync/sessions/{sessionId}/ack", new { checkpoint = Convert.ToBase64String(next) });
        await Assert.That(ack).IsEqualTo(200);

        // A snapshot pull answers the full state and a fresh checkpoint.
        var (snapshot, snapshotBody) = await HostHarness.SendAsync(caller, $"/v1/sync/sessions/{sessionId}/snapshot", body: new { }, method: "POST");
        await Assert.That(snapshot).IsEqualTo(200);
        await Assert.That(snapshotBody.RootElement.GetProperty("payloadCodec").GetString()).IsEqualTo("loro-snapshot/1");
        await Assert.That(Convert.FromBase64String(snapshotBody.RootElement.GetProperty("checkpoint").GetString()!)).IsNotEmpty();

        // The session closes; the audit carries both lifecycle facts.
        var (close, _) = await HostHarness.SendAsync(caller, $"/v1/sync/sessions/{sessionId}/close", method: "POST");
        await Assert.That(close).IsEqualTo(200);
        var audit = host.Audit.Recent(32);
        await Assert.That(audit.Any(record => record.Kind == AuditKind.SessionOpened && record.Tenant == "tenant-a")).IsTrue();
        await Assert.That(audit.Any(record => record.Kind == AuditKind.SessionClosed && record.Detail.Contains($"session {sessionId}", StringComparison.Ordinal))).IsTrue();
        await Assert.That(audit.Any(record => record.Kind == AuditKind.MutationApplied && record.Operation == "sync.mutation_submit")).IsTrue();

        // A lifecycle frame on the closed session is answered as ended.
        var (afterClose, afterBody) = await HostHarness.SendAsync(
            caller,
            $"/v1/sync/sessions/{sessionId}/delta",
            new { from = Convert.ToBase64String(next) });
        await Assert.That(afterClose).IsEqualTo(410);
        await Assert.That(afterBody.RootElement.GetProperty("code").GetString()).IsEqualTo("session-ended");
    }

    /// <summary>A retried idempotency key answers the recorded receipt — the at-least-once retry identity.</summary>
    [Test]
    public async Task ARetriedIdempotencyKeyAnswersTheRecordedReceipt()
    {
        var path = SocketPath();
        await using var engine = new ScriptedTbepEngine(path);
        await using var host = await HostHarness.StartAsync(new EngineOptions { SocketPath = path });
        var caller = host.As("backend-operator", "tenant-a");
        var (sessionId, _) = await OpenSessionAsync(caller);
        var mutation = new
        {
            document = "d-01",
            change = new { put = new[] { new { name = "qty", value = new { integer = 1L } } } },
        };

        var (first, firstBody) = await HostHarness.SendAsync(caller, $"/v1/sync/sessions/{sessionId}/mutations", mutation, idempotencyKey: "retry-me");
        await Assert.That(first).IsEqualTo(200);
        var firstReceipt = firstBody.RootElement.GetProperty("outcome").GetProperty("receipt");

        var (second, secondBody) = await HostHarness.SendAsync(caller, $"/v1/sync/sessions/{sessionId}/mutations", mutation, idempotencyKey: "retry-me");
        await Assert.That(second).IsEqualTo(200);
        var secondReceipt = secondBody.RootElement.GetProperty("outcome").GetProperty("receipt");
        await Assert.That(secondReceipt.GetProperty("sequence").GetUInt64()).IsEqualTo(firstReceipt.GetProperty("sequence").GetUInt64());

        // The dedup is engine state, not host state: one commit landed.
        var (_, statusBody) = await HostHarness.SendAsync(caller, "/v1/engine/status");
        var frames = statusBody.RootElement.GetProperty("sessions").GetUInt32();
        await Assert.That(frames).IsGreaterThanOrEqualTo(1u);
    }

    /// <summary>Composite field values are typed rejections on the sync-serving domain, never coerced.</summary>
    [Test]
    public async Task CompositeFieldValuesAreRejectedNeverCoerced()
    {
        var path = SocketPath();
        await using var engine = new ScriptedTbepEngine(path);
        await using var host = await HostHarness.StartAsync(new EngineOptions { SocketPath = path });
        var caller = host.As("backend-operator", "tenant-a");
        var (sessionId, _) = await OpenSessionAsync(caller);

        var (composite, compositeBody) = await HostHarness.SendAsync(
            caller,
            $"/v1/sync/sessions/{sessionId}/mutations",
            new { document = "d-01", change = new { put = new[] { new { name = "items", value = new { array = Array.Empty<object>() } } } } },
            idempotencyKey: "op-composite");
        await Assert.That(composite).IsEqualTo(200);
        await Assert.That(compositeBody.RootElement.GetProperty("outcome").GetProperty("outcome").GetString()).IsEqualTo("rejected");
        await Assert.That(compositeBody.RootElement.GetProperty("outcome").GetProperty("reason").GetString()).IsEqualTo("invalid_body");
        await Assert.That(host.Audit.Recent(8).Any(record => record.Kind == AuditKind.MutationRejected)).IsTrue();
    }

    /// <summary>Checkpoint refusals are typed conflicts with the resync marker.</summary>
    [Test]
    public async Task CheckpointRefusalsAreTypedConflictsWithTheResyncMarker()
    {
        var path = SocketPath();
        await using var engine = new ScriptedTbepEngine(path);
        await using var host = await HostHarness.StartAsync(new EngineOptions { SocketPath = path });
        var caller = host.As("backend-operator", "tenant-a");
        var (sessionId, checkpoint) = await OpenSessionAsync(caller);

        // A checkpoint the engine does not hold: refused, resync required.
        var stale = new byte[] { 0xee, 0x6f, 0x70 };
        var (delta, deltaBody) = await HostHarness.SendAsync(
            caller,
            $"/v1/sync/sessions/{sessionId}/delta",
            new { from = Convert.ToBase64String(stale) });
        await Assert.That(delta).IsEqualTo(409);
        await Assert.That(deltaBody.RootElement.GetProperty("code").GetString()).IsEqualTo("checkpoint-refused");
        await Assert.That(deltaBody.RootElement.GetProperty("resyncRequired").GetBoolean()).IsTrue();

        // The resync path: a snapshot pull answers the fresh state.
        var (snapshot, snapshotBody) = await HostHarness.SendAsync(caller, $"/v1/sync/sessions/{sessionId}/snapshot", body: new { }, method: "POST");
        await Assert.That(snapshot).IsEqualTo(200);
        var fresh = Convert.FromBase64String(snapshotBody.RootElement.GetProperty("checkpoint").GetString()!);

        // The delta from the fresh checkpoint is up to date.
        var (resumed, resumedBody) = await HostHarness.SendAsync(
            caller,
            $"/v1/sync/sessions/{sessionId}/delta",
            new { from = Convert.ToBase64String(fresh) });
        await Assert.That(resumed).IsEqualTo(200);
        await Assert.That(resumedBody.RootElement.GetProperty("commits").GetUInt32()).IsEqualTo(0u);

        // A bad checkpoint acknowledgement is refused the same way.
        var (badAck, badAckBody) = await HostHarness.SendAsync(
            caller,
            $"/v1/sync/sessions/{sessionId}/ack",
            new { checkpoint = Convert.ToBase64String([0xde, 0xad, 0xbe]) });
        await Assert.That(badAck).IsEqualTo(409);
        await Assert.That(badAckBody.RootElement.GetProperty("code").GetString()).IsEqualTo("checkpoint-refused");
    }

    /// <summary>A resume checkpoint that does not decode is refused fail-closed at setup.</summary>
    [Test]
    public async Task AMalformedResumeCheckpointIsRefusedFailClosed()
    {
        var path = SocketPath();
        await using var engine = new ScriptedTbepEngine(path);
        await using var host = await HostHarness.StartAsync(new EngineOptions { SocketPath = path });
        var caller = host.As("backend-operator", "tenant-a");

        var (status, body) = await HostHarness.SendAsync(
            caller,
            "/v1/sync/sessions",
            new { collection = "orders", resume = Convert.ToBase64String([0xde, 0xad, 0xbe, 0xef]) });
        await Assert.That(status).IsEqualTo(409);
        await Assert.That(body.RootElement.GetProperty("code").GetString()).IsEqualTo("checkpoint-refused");
    }

    /// <summary>
    /// The engine ending a session on its own (no generation loss the
    /// host would hear) stops the host's tracking: the refusal that
    /// proves it is not live removes it — nothing lingers as a session
    /// the host no longer owns.
    /// </summary>
    [Test]
    public async Task AnEngineEndedSessionStopsBeingTracked()
    {
        var path = SocketPath();
        await using var engine = new ScriptedTbepEngine(path);
        await using var host = await HostHarness.StartAsync(new EngineOptions { SocketPath = path });
        var caller = host.As("backend-operator", "tenant-a");
        var (sessionId, checkpoint) = await OpenSessionAsync(caller);
        await Assert.That(host.Supervisor.Sessions.ContainsKey(sessionId)).IsTrue();

        // The engine ends the session itself (an idle expiry, say): no
        // refusal, no engine-generation loss — the host hears nothing.
        engine.ForgetSession(sessionId);

        // The next lifecycle frame is the engine's typed refusal —
        // unknown, not cross-tenant — and the host stops tracking the
        // session it now knows is not live on the engine.
        var (ended, endedBody) = await HostHarness.SendAsync(
            caller,
            $"/v1/sync/sessions/{sessionId}/delta",
            new { from = Convert.ToBase64String(checkpoint) });
        await Assert.That(ended).IsEqualTo(410);
        await Assert.That(endedBody.RootElement.GetProperty("code").GetString()).IsEqualTo("session-ended");
        await Assert.That(host.Supervisor.Sessions.ContainsKey(sessionId)).IsFalse();

        // The registry answers truthfully too: the session is gone, not
        // lingering as live state the engine already ended.
        var (listing, listingBody) = await HostHarness.SendAsync(caller, "/v1/sync/sessions");
        await Assert.That(listing).IsEqualTo(200);
        await Assert.That(listingBody.RootElement.EnumerateArray().Count()).IsEqualTo(0);
    }

    /// <summary>A session id this host does not track is answered as ended, without touching the engine.</summary>
    [Test]
    public async Task AnUntrackedSessionIsAnsweredAsEndedWithoutTouchingTheEngine()
    {
        var path = SocketPath();
        await using var engine = new ScriptedTbepEngine(path);
        await using var host = await HostHarness.StartAsync(new EngineOptions { SocketPath = path });
        var caller = host.As("backend-operator", "tenant-a");
        var before = engine.Received.Count;

        var (status, body) = await HostHarness.SendAsync(
            caller,
            "/v1/sync/sessions/9999/delta",
            new { from = Convert.ToBase64String([0, 0x6f, 0x70]) });
        await Assert.That(status).IsEqualTo(410);
        await Assert.That(body.RootElement.GetProperty("code").GetString()).IsEqualTo("session-ended");
        await Assert.That(engine.Received.Count).IsEqualTo(before);
        await Assert.That(host.Audit.Recent(8).Any(record => record.Kind == AuditKind.SessionEndedByRestart)).IsTrue();
    }
}

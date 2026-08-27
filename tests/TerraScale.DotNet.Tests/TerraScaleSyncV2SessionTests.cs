using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using TerraScale.DotNet;

namespace TerraScale.DotNet.Tests;

/// <summary>
/// The SE-10 session-surface integration rows: the native managed
/// <c>terrascale.sync.v2</c> client speaking real HTTP to a loopback host
/// that serves the C# API host's documented sync-session and stream
/// endpoints. The rows prove the trigger-driven delta pulls (push the
/// hint, pull the delta — no polling), checkpoint resume on reconnect,
/// the resync-required snapshot path, server-ack-only mutations, and the
/// typed failure surfaces.
/// </summary>
public sealed class TerraScaleSyncV2SessionTests
{
    private static byte[] CheckpointBytes(ulong sequence, int end = 6) =>
        TerraScaleSyncV2Checkpoint.Create([(1ul, end)], sequence).ToBytes();

    private static HttpClient CreateClient(LoopbackSyncV2Host host, string principal = "backend-operator", string tenant = "tenant-a") =>
        new() { BaseAddress = new Uri(host.BaseAddress), Timeout = TimeSpan.FromSeconds(10) };

    private static TerraScaleSyncV2Client CreateSyncClient(HttpClient http, string principal = "backend-operator", string tenant = "tenant-a") =>
        new(http, new TerraScaleSyncV2ClientOptions { Principal = principal, Tenant = tenant });

    /// <summary>Setup sends the collection (and a resume checkpoint when the session resumes).</summary>
    [Test]
    public async Task SetupSendsTheCollectionAndTheResumeCheckpoint()
    {
        using var host = await LoopbackSyncV2Host.StartAsync();
        host.Script.SetupAnswer = new TerraScaleSyncV2SessionAccepted(7ul,
            TerraScaleSyncV2Checkpoint.Create([(1ul, 6)], 3),
            TerraScaleSyncV2Checkpoint.Create([(1ul, 2)], 1));
        using var http = CreateClient(host);
        await using var session = CreateSyncClient(http).CreateSession(new TerraScaleSyncV2SessionOptions("orders"));

        var accepted = await session.SetupAsync();

        await Assert.That(accepted.SessionId).IsEqualTo(7ul);
        await Assert.That(accepted.ServerCheckpoint).IsEqualTo(TerraScaleSyncV2Checkpoint.Create([(1ul, 6)], 3));
        await Assert.That(accepted.RetainedFloor).IsEqualTo(TerraScaleSyncV2Checkpoint.Create([(1ul, 2)], 1));
        var setup = await host.NextRequestAsync();
        await Assert.That(setup.Method).IsEqualTo("POST");
        await Assert.That(setup.Path).IsEqualTo("/v1/sync/sessions");
        await Assert.That(setup.Header("X-TB-Principal")).IsEqualTo("backend-operator");
        await Assert.That(setup.Header("X-TB-Tenant")).IsEqualTo("tenant-a");
        await Assert.That(setup.Json().GetProperty("collection").GetString()).IsEqualTo("orders");
        await Assert.That(setup.Json().TryGetProperty("resume", out _)).IsFalse();

        // A resuming session presents its durable checkpoint.
        using var resumingHost = await LoopbackSyncV2Host.StartAsync();
        resumingHost.Script.SetupAnswer = accepted;
        using var resumingHttp = CreateClient(resumingHost);
        var resume = TerraScaleSyncV2Checkpoint.Create([(1ul, 6)], 3);
        await using var resuming = CreateSyncClient(resumingHttp).CreateSession(new TerraScaleSyncV2SessionOptions("orders") { Resume = resume });
        _ = await resuming.SetupAsync();
        var resumeSetup = await resumingHost.NextRequestAsync();
        await Assert.That(Convert.FromBase64String(resumeSetup.Json().GetProperty("resume").GetString()!))
            .IsEquivalentTo(resume.ToBytes());
    }

    /// <summary>A fresh session adopts the server snapshot and acknowledges its checkpoint.</summary>
    [Test]
    public async Task AFreshSessionAdoptsTheSnapshotAndAcknowledgesItsCheckpoint()
    {
        using var host = await LoopbackSyncV2Host.StartAsync();
        var serverCheckpoint = TerraScaleSyncV2Checkpoint.Create([(1ul, 6)], 3);
        host.Script.SetupAnswer = new TerraScaleSyncV2SessionAccepted(7ul, serverCheckpoint, TerraScaleSyncV2Checkpoint.Initial);
        host.Script.SnapshotAnswer = new TerraScaleSyncV2SnapshotPull(
            TerraScaleSyncV2PayloadCodec.LoroSnapshot1, [1, 2, 3, 4], serverCheckpoint);
        using var http = CreateClient(host);
        await using var session = CreateSyncClient(http).CreateSession(new TerraScaleSyncV2SessionOptions("orders"));
        _ = await session.SetupAsync();
        _ = await host.NextRequestAsync();

        var snapshot = await session.PullSnapshotAsync();
        _ = await host.NextRequestAsync();

        await Assert.That(snapshot.Codec).IsEqualTo(TerraScaleSyncV2PayloadCodec.LoroSnapshot1);
        await Assert.That(snapshot.Payload.ToArray()).IsEquivalentTo((byte[])[1, 2, 3, 4]);
        await Assert.That(session.State.Checkpoint).IsEqualTo(serverCheckpoint);
        await Assert.That(session.State.DeltasApplied).IsEqualTo(0ul);

        await session.AcknowledgeCheckpointAsync();
        var ack = await host.NextRequestAsync();
        await Assert.That(ack.Path).IsEqualTo("/v1/sync/sessions/7/ack");
        await Assert.That(Convert.FromBase64String(ack.Json().GetProperty("checkpoint").GetString()!))
            .IsEquivalentTo(serverCheckpoint.ToBytes());
    }

    /// <summary>Trigger notifications drive immediate delta pulls: no polling, no delta request before the hint.</summary>
    [Test]
    public async Task TriggersDriveImmediateDeltaPullsWithoutPolling()
    {
        using var host = await LoopbackSyncV2Host.StartAsync();
        var start = TerraScaleSyncV2Checkpoint.Create([(1ul, 6)], 3);
        var next = TerraScaleSyncV2Checkpoint.Create([(1ul, 9)], 4);
        host.Script.SetupAnswer = new TerraScaleSyncV2SessionAccepted(7ul, start, TerraScaleSyncV2Checkpoint.Initial);
        host.Script.SnapshotAnswer = new TerraScaleSyncV2SnapshotPull(TerraScaleSyncV2PayloadCodec.LoroSnapshot1, [9, 9], start);
        host.Script.DeltaAnswers.Enqueue(new TerraScaleSyncV2DeltaPull(
            TerraScaleSyncV2DeltaPullKind.Delta,
            TerraScaleSyncV2PayloadCodec.LoroDelta1,
            [0xaa, 0xbb],
            next,
            Commits: 2,
            SingleCommitOversize: false,
            Floor: null));
        host.Script.DeltaAnswers.Enqueue(new TerraScaleSyncV2DeltaPull(
            TerraScaleSyncV2DeltaPullKind.UpToDate, TerraScaleSyncV2PayloadCodec.LoroDelta1, [], start, 0, false, null));
        using var http = CreateClient(host);
        await using var session = CreateSyncClient(http).CreateSession(new TerraScaleSyncV2SessionOptions("orders"));

        var updates = session.UpdatesAsync().GetAsyncEnumerator();
        await Assert.That(await updates.MoveNextAsync()).IsTrue();
        await Assert.That(updates.Current.Kind).IsEqualTo(TerraScaleSyncV2SyncUpdateKind.SnapshotAdopted);
        await Assert.That(updates.Current.Checkpoint).IsEqualTo(start);

        // Driving the enumerator opens the stream: setup, snapshot, and
        // the snapshot acknowledgement are recorded first.
        var nextUpdate = updates.MoveNextAsync().AsTask();
        await Assert.That((await host.NextRequestAsync()).Path).IsEqualTo("/v1/sync/sessions");
        await Assert.That((await host.NextRequestAsync()).Path).IsEqualTo("/v1/sync/sessions/7/snapshot");
        await Assert.That((await host.NextRequestAsync()).Path).IsEqualTo("/v1/sync/sessions/7/ack");
        var streamRequest = await host.NextRequestAsync();
        await Assert.That(streamRequest.Path).IsEqualTo("/v1/streams");
        await Assert.That(streamRequest.Query).IsEqualTo("collections=orders");
        var connection = await host.NextStreamConnectionAsync();

        // No delta request arrives while there is no trigger: the pull is hint-driven.
        await Task.Delay(200);
        await Assert.That(host.HasPendingRequests).IsFalse();
        await Assert.That(session.State.DeltasApplied).IsEqualTo(0ul);

        await connection.TriggerAsync(id: 5, collection: "orders", sequence: 4, operationId: "op-000004");
        await Assert.That(await nextUpdate).IsTrue();
        await Assert.That(updates.Current.Kind).IsEqualTo(TerraScaleSyncV2SyncUpdateKind.DeltaApplied);
        await Assert.That(updates.Current.Payload.ToArray()).IsEquivalentTo((byte[])[0xaa, 0xbb]);
        await Assert.That(updates.Current.Commits).IsEqualTo(2ul);
        await Assert.That(updates.Current.Checkpoint).IsEqualTo(next);

        var delta = await host.NextRequestAsync();
        await Assert.That(delta.Path).IsEqualTo("/v1/sync/sessions/7/delta");
        await Assert.That(Convert.FromBase64String(delta.Json().GetProperty("from").GetString()!).SequenceEqual(start.ToBytes())).IsTrue();
        var ack = await host.NextRequestAsync();
        await Assert.That(ack.Path).IsEqualTo("/v1/sync/sessions/7/ack");
        await Assert.That(Convert.FromBase64String(ack.Json().GetProperty("checkpoint").GetString()!).SequenceEqual(next.ToBytes())).IsTrue();

        await Assert.That(session.State.TriggersObserved).IsEqualTo(1ul);
        await Assert.That(session.State.DeltasApplied).IsEqualTo(1ul);
        await Assert.That(session.State.DeliveryBytes).IsEqualTo(2ul);
        await DisposeEnumeratorAsync(updates);
    }

    /// <summary>At-least-once re-delivery of an already-absorbed batch is a detected duplicate, not a second application.</summary>
    [Test]
    public async Task RedeliveredDeltasAreDetectedDuplicates()
    {
        using var host = await LoopbackSyncV2Host.StartAsync();
        var start = TerraScaleSyncV2Checkpoint.Create([(1ul, 6)], 3);
        var next = TerraScaleSyncV2Checkpoint.Create([(1ul, 9)], 4);
        host.Script.SetupAnswer = new TerraScaleSyncV2SessionAccepted(7ul, start, TerraScaleSyncV2Checkpoint.Initial);
        host.Script.SnapshotAnswer = new TerraScaleSyncV2SnapshotPull(TerraScaleSyncV2PayloadCodec.LoroSnapshot1, [9], start);
        host.Script.DeltaAnswers.Enqueue(new TerraScaleSyncV2DeltaPull(TerraScaleSyncV2DeltaPullKind.Delta, TerraScaleSyncV2PayloadCodec.LoroDelta1, [0x01], next, 1, false, null));
        host.Script.DeltaAnswers.Enqueue(new TerraScaleSyncV2DeltaPull(TerraScaleSyncV2DeltaPullKind.Delta, TerraScaleSyncV2PayloadCodec.LoroDelta1, [0x01], next, 1, false, null));
        using var http = CreateClient(host);
        await using var session = CreateSyncClient(http).CreateSession(new TerraScaleSyncV2SessionOptions("orders"));
        var updates = session.UpdatesAsync().GetAsyncEnumerator();
        await Assert.That(await updates.MoveNextAsync()).IsTrue();

        var firstUpdate = updates.MoveNextAsync().AsTask();
        _ = await host.NextRequestAsync();
        _ = await host.NextRequestAsync();
        _ = await host.NextRequestAsync();
        _ = await host.NextRequestAsync();
        var connection = await host.NextStreamConnectionAsync();
        await connection.TriggerAsync(5, "orders", 4, "op-000004");
        await Assert.That(await firstUpdate).IsTrue();
        await Assert.That(updates.Current.Kind).IsEqualTo(TerraScaleSyncV2SyncUpdateKind.DeltaApplied);

        // The same batch is re-served (reconnect replay): a duplicate, not a re-application.
        var secondUpdate = updates.MoveNextAsync().AsTask();
        await connection.TriggerAsync(6, "orders", 4, "op-000004");
        await Assert.That(await secondUpdate).IsTrue();
        await Assert.That(updates.Current.Kind).IsEqualTo(TerraScaleSyncV2SyncUpdateKind.DuplicateDelta);
        await Assert.That(session.State.DeltasApplied).IsEqualTo(1ul);
        await Assert.That(session.State.DuplicateDeltas).IsEqualTo(1ul);
        await Assert.That(session.State.Checkpoint).IsEqualTo(next);
        await DisposeEnumeratorAsync(updates);
    }

    /// <summary>A behind-retained-floor session is told to resync and adopts a full snapshot.</summary>
    [Test]
    public async Task ResyncRequiredAdoptsAFullSnapshot()
    {
        using var host = await LoopbackSyncV2Host.StartAsync();
        var start = TerraScaleSyncV2Checkpoint.Create([(1ul, 6)], 3);
        var current = TerraScaleSyncV2Checkpoint.Create([(1ul, 40)], 22);
        host.Script.SetupAnswer = new TerraScaleSyncV2SessionAccepted(7ul, start, TerraScaleSyncV2Checkpoint.Initial);
        host.Script.SnapshotAnswer = new TerraScaleSyncV2SnapshotPull(TerraScaleSyncV2PayloadCodec.LoroSnapshot1, [7, 7, 7], current);
        host.Script.DeltaAnswers.Enqueue(new TerraScaleSyncV2DeltaPull(
            TerraScaleSyncV2DeltaPullKind.ResyncRequired,
            TerraScaleSyncV2PayloadCodec.LoroDelta1,
            [],
            null!,
            0,
            false,
            TerraScaleSyncV2Checkpoint.Create([(1ul, 30)], 20)));
        using var http = CreateClient(host);
        await using var session = CreateSyncClient(http).CreateSession(new TerraScaleSyncV2SessionOptions("orders"));
        var updates = session.UpdatesAsync().GetAsyncEnumerator();
        await Assert.That(await updates.MoveNextAsync()).IsTrue();

        var resyncUpdate = updates.MoveNextAsync().AsTask();
        _ = await host.NextRequestAsync();
        _ = await host.NextRequestAsync();
        _ = await host.NextRequestAsync();
        _ = await host.NextRequestAsync();
        var connection = await host.NextStreamConnectionAsync();

        await connection.TriggerAsync(5, "orders", 23, "op-23");
        await Assert.That(await resyncUpdate).IsTrue();
        await Assert.That(updates.Current.Kind).IsEqualTo(TerraScaleSyncV2SyncUpdateKind.ResyncAdopted);
        await Assert.That(session.State.Checkpoint).IsEqualTo(current);
        // The initial full-state adoption plus the forced resync: two snapshots adopted.
        await Assert.That(session.State.ResyncsAdopted).IsEqualTo(2ul);

        // The adopted checkpoint is acknowledged: retransmission stays checkpoint-bounded.
        var requests = await host.DrainRequestsAsync();
        await Assert.That(requests.Any(request =>
            request.Path == "/v1/sync/sessions/7/ack" &&
            Convert.FromBase64String(request.Json().GetProperty("checkpoint").GetString()!).SequenceEqual(current.ToBytes()))).IsTrue();
        await DisposeEnumeratorAsync(updates);
    }

    /// <summary>Mutations are server-ack-only: the receipt returns, nothing applies locally, retries answer the same receipt.</summary>
    [Test]
    public async Task MutationsAreServerAckOnlyAndNeverApplyLocally()
    {
        using var host = await LoopbackSyncV2Host.StartAsync();
        var start = TerraScaleSyncV2Checkpoint.Create([(1ul, 6)], 3);
        host.Script.SetupAnswer = new TerraScaleSyncV2SessionAccepted(7ul, start, TerraScaleSyncV2Checkpoint.Initial);
        host.Script.MutationAnswer = new TerraScaleSyncV2MutationPull(
            new TerraScaleSyncV2MutationApplied(11ul, "op-01"));
        using var http = CreateClient(host);
        await using var session = CreateSyncClient(http).CreateSession(new TerraScaleSyncV2SessionOptions("orders"));
        _ = await session.SetupAsync();
        _ = await host.NextRequestAsync();

        var outcome = await session.MutateAsync(
            "d-01",
            new TerraScaleSyncV2DocumentWrite.Put(new Dictionary<string, TerraScaleSyncV2Value> { ["qty"] = TerraScaleSyncV2Value.Of(3L) }),
            "op-01");

        await Assert.That(outcome).IsTypeOf<TerraScaleSyncV2MutationApplied>();
        var applied = (TerraScaleSyncV2MutationApplied)outcome;
        await Assert.That(applied.Sequence).IsEqualTo(11ul);
        await Assert.That(applied.OperationId).IsEqualTo("op-01");

        var mutation = await host.NextRequestAsync();
        await Assert.That(mutation.Header("Idempotency-Key")).IsEqualTo("op-01");
        await Assert.That(mutation.Json().GetProperty("document").GetString()).IsEqualTo("d-01");
        await Assert.That(mutation.Json().GetProperty("change").GetProperty("put")[0].GetProperty("name").GetString()).IsEqualTo("qty");
        await Assert.That(mutation.Json().GetProperty("change").GetProperty("put")[0].GetProperty("value").GetProperty("integer").GetInt64()).IsEqualTo(3L);

        // Server-ack-only: the local checkpoint did not move (nothing has
        // been absorbed); the change arrives as delivery.
        await Assert.That(session.State.Checkpoint).IsEqualTo(TerraScaleSyncV2Checkpoint.Initial);
        await Assert.That(session.State.DeltasApplied).IsEqualTo(0ul);
        await Assert.That(session.State.MutationsAcknowledged).IsEqualTo(1ul);

        // A retried key answers the recorded receipt.
        var retry = await session.MutateAsync("d-01", new TerraScaleSyncV2DocumentWrite.Delete(), "op-01");
        var retried = (TerraScaleSyncV2MutationApplied)retry;
        await Assert.That(retried.Sequence).IsEqualTo(11ul);
        await Assert.That(session.State.MutationsAcknowledged).IsEqualTo(2ul);

        _ = await Assert.ThrowsAsync<TerraScaleSyncV2ProtocolException>(
            () => session.MutateAsync("d-01", new TerraScaleSyncV2DocumentWrite.Delete(), " "));
    }

    /// <summary>Typed rejections surface as rejections, never as silent successes.</summary>
    [Test]
    public async Task RejectedMutationsSurfaceTheTypedReason()
    {
        using var host = await LoopbackSyncV2Host.StartAsync();
        host.Script.SetupAnswer = new TerraScaleSyncV2SessionAccepted(7ul, TerraScaleSyncV2Checkpoint.Initial, TerraScaleSyncV2Checkpoint.Initial);
        host.Script.MutationAnswer = new TerraScaleSyncV2MutationPull(
            new TerraScaleSyncV2MutationRejected(TerraScaleSyncV2ErrorCode.SessionClosed));
        using var http = CreateClient(host);
        await using var session = CreateSyncClient(http).CreateSession(new TerraScaleSyncV2SessionOptions("orders"));
        _ = await session.SetupAsync();
        _ = await host.NextRequestAsync();

        var outcome = await session.MutateAsync("d-01", new TerraScaleSyncV2DocumentWrite.Delete(), "op-reject");
        await Assert.That(outcome).IsTypeOf<TerraScaleSyncV2MutationRefusal>();
        var refusal = (TerraScaleSyncV2MutationRefusal)outcome;
        await Assert.That(refusal.Reason).IsEqualTo("session_closed");
        await Assert.That(refusal.Code).IsEqualTo(TerraScaleSyncV2ErrorCode.SessionClosed);

        // A rejection is never an acknowledged mutation: only applied outcomes count.
        await Assert.That(session.State.MutationsAcknowledged).IsEqualTo(0ul);
    }

    /// <summary>A dropped stream reconnects with the Last-Event-ID of the last observed trigger.</summary>
    [Test]
    public async Task ADroppedStreamReconnectsWithTheLastEventId()
    {
        using var host = await LoopbackSyncV2Host.StartAsync();
        var start = TerraScaleSyncV2Checkpoint.Create([(1ul, 6)], 3);
        var next = TerraScaleSyncV2Checkpoint.Create([(1ul, 8)], 4);
        host.Script.SetupAnswer = new TerraScaleSyncV2SessionAccepted(7ul, start, TerraScaleSyncV2Checkpoint.Initial);
        host.Script.SnapshotAnswer = new TerraScaleSyncV2SnapshotPull(TerraScaleSyncV2PayloadCodec.LoroSnapshot1, [5], start);
        host.Script.DeltaAnswers.Enqueue(new TerraScaleSyncV2DeltaPull(TerraScaleSyncV2DeltaPullKind.Delta, TerraScaleSyncV2PayloadCodec.LoroDelta1, [0x11], next, 1, false, null));
        using var http = CreateClient(host);
        await using var session = CreateSyncClient(http).CreateSession(new TerraScaleSyncV2SessionOptions("orders")
        {
            ReconnectDelay = TimeSpan.FromMilliseconds(30),
        });
        var updates = session.UpdatesAsync().GetAsyncEnumerator();
        await Assert.That(await updates.MoveNextAsync()).IsTrue();

        var deltaUpdate = updates.MoveNextAsync().AsTask();
        _ = await host.NextRequestAsync();
        _ = await host.NextRequestAsync();
        _ = await host.NextRequestAsync();
        _ = await host.NextRequestAsync();
        var first = await host.NextStreamConnectionAsync();
        await first.TriggerAsync(5, "orders", 4, "op-4");
        await Assert.That(await deltaUpdate).IsTrue();
        await Assert.That(updates.Current.Kind).IsEqualTo(TerraScaleSyncV2SyncUpdateKind.DeltaApplied);
        _ = await host.NextRequestAsync();
        _ = await host.NextRequestAsync();
        _ = await host.NextRequestAsync();

        first.Close();
        var reconnecting = updates.MoveNextAsync().AsTask();
        var reconnect = await host.NextRequestAsync();
        await Assert.That(reconnect.Path).IsEqualTo("/v1/streams");
        await Assert.That(reconnect.Header("Last-Event-ID")).IsEqualTo("5");

        // The reconnecting move waits for its next trigger: disposal faults it.
        await session.DisposeAsync();
        _ = await Assert.ThrowsAsync<OperationCanceledException>(() => reconnecting);
        await DisposeEnumeratorAsync(updates);
    }

    /// <summary>An ended session is re-opened by resuming from the durable checkpoint.</summary>
    [Test]
    public async Task AnEndedSessionIsResumedFromTheDurableCheckpoint()
    {
        using var host = await LoopbackSyncV2Host.StartAsync();
        var start = TerraScaleSyncV2Checkpoint.Create([(1ul, 6)], 3);
        var next = TerraScaleSyncV2Checkpoint.Create([(1ul, 8)], 4);
        host.Script.SetupAnswer = new TerraScaleSyncV2SessionAccepted(7ul, start, TerraScaleSyncV2Checkpoint.Initial);
        host.Script.SnapshotAnswer = new TerraScaleSyncV2SnapshotPull(TerraScaleSyncV2PayloadCodec.LoroSnapshot1, [5], start);
        host.Script.DeltaAnswers.Enqueue(new TerraScaleSyncV2DeltaPull(TerraScaleSyncV2DeltaPullKind.Delta, TerraScaleSyncV2PayloadCodec.LoroDelta1, [0x11], next, 1, false, null));
        using var http = CreateClient(host);
        await using var session = CreateSyncClient(http).CreateSession(new TerraScaleSyncV2SessionOptions("orders"));
        var updates = session.UpdatesAsync().GetAsyncEnumerator();
        await Assert.That(await updates.MoveNextAsync()).IsTrue();

        var deltaUpdate = updates.MoveNextAsync().AsTask();
        _ = await host.NextRequestAsync();
        _ = await host.NextRequestAsync();
        _ = await host.NextRequestAsync();
        _ = await host.NextRequestAsync();
        var first = await host.NextStreamConnectionAsync();
        await first.TriggerAsync(5, "orders", 4, "op-4");
        await Assert.That(await deltaUpdate).IsTrue();

        // The host answers 410 on the next delta: the session is gone, so it re-opens and resumes.
        host.Script.SessionEnded = true;
        host.Script.SetupAnswer = new TerraScaleSyncV2SessionAccepted(8ul, next, TerraScaleSyncV2Checkpoint.Initial);
        host.Script.DeltaAnswers.Enqueue(new TerraScaleSyncV2DeltaPull(TerraScaleSyncV2DeltaPullKind.UpToDate, TerraScaleSyncV2PayloadCodec.LoroDelta1, [], next, 0, false, null));
        var recovery = updates.MoveNextAsync().AsTask();
        await first.TriggerAsync(6, "orders", 5, "op-5");
        await Task.Delay(500);
        var requests = await host.DrainRequestsAsync();
        var resumeSetup = requests.FirstOrDefault(request => request.Path == "/v1/sync/sessions" && request.Json().TryGetProperty("resume", out _));
        await Assert.That(resumeSetup).IsNotNull();
        await Assert.That(Convert.FromBase64String(resumeSetup!.Json().GetProperty("resume").GetString()!).SequenceEqual(next.ToBytes())).IsTrue();
        await session.DisposeAsync();
        _ = await Assert.ThrowsAsync<OperationCanceledException>(() => recovery);
        await DisposeEnumeratorAsync(updates);
    }

    /// <summary>A retryable stream-open failure (an engine blip) reconnects instead of faulting the live loop.</summary>
    [Test]
    public async Task ARetryableStreamFailureReconnectsInsteadOfFaulting()
    {
        using var host = await LoopbackSyncV2Host.StartAsync();
        var start = TerraScaleSyncV2Checkpoint.Create([(1ul, 6)], 3);
        var next = TerraScaleSyncV2Checkpoint.Create([(1ul, 8)], 4);
        host.Script.SetupAnswer = new TerraScaleSyncV2SessionAccepted(7ul, start, TerraScaleSyncV2Checkpoint.Initial);
        host.Script.SnapshotAnswer = new TerraScaleSyncV2SnapshotPull(TerraScaleSyncV2PayloadCodec.LoroSnapshot1, [5], start);
        host.Script.DeltaAnswers.Enqueue(new TerraScaleSyncV2DeltaPull(TerraScaleSyncV2DeltaPullKind.Delta, TerraScaleSyncV2PayloadCodec.LoroDelta1, [0x11], next, 1, false, null));
        host.Script.DeltaAnswers.Enqueue(new TerraScaleSyncV2DeltaPull(TerraScaleSyncV2DeltaPullKind.UpToDate, TerraScaleSyncV2PayloadCodec.LoroDelta1, [], next, 0, false, null));
        host.Script.StreamRefusalsRemaining = 1;
        using var http = CreateClient(host);
        await using var session = CreateSyncClient(http).CreateSession(new TerraScaleSyncV2SessionOptions("orders")
        {
            ReconnectDelay = TimeSpan.FromMilliseconds(30),
        });
        var updates = session.UpdatesAsync().GetAsyncEnumerator();
        await Assert.That(await updates.MoveNextAsync()).IsTrue();

        var deltaUpdate = updates.MoveNextAsync().AsTask();
        _ = await host.NextRequestAsync();
        _ = await host.NextRequestAsync();
        _ = await host.NextRequestAsync();
        _ = await host.NextRequestAsync();

        // The refused open (503: the engine generation is down) is answered
        // by a reconnect, never by faulting the enumeration.
        var reconnect = await host.NextRequestAsync().WaitAsync(TimeSpan.FromSeconds(2));
        await Assert.That(reconnect.Path).IsEqualTo("/v1/streams");
        var connection = await host.NextStreamConnectionAsync();
        await connection.TriggerAsync(5, "orders", 4, "op-4");
        await Assert.That(await deltaUpdate).IsTrue();
        await Assert.That(updates.Current.Kind).IsEqualTo(TerraScaleSyncV2SyncUpdateKind.DeltaApplied);
        await DisposeEnumeratorAsync(updates);
    }

    /// <summary>A malformed trigger hint is content-free noise: it counts nothing and never kills the loop.</summary>
    [Test]
    public async Task AMalformedTriggerHintDoesNotKillTheLoop()
    {
        using var host = await LoopbackSyncV2Host.StartAsync();
        var start = TerraScaleSyncV2Checkpoint.Create([(1ul, 6)], 3);
        var next = TerraScaleSyncV2Checkpoint.Create([(1ul, 8)], 4);
        host.Script.SetupAnswer = new TerraScaleSyncV2SessionAccepted(7ul, start, TerraScaleSyncV2Checkpoint.Initial);
        host.Script.SnapshotAnswer = new TerraScaleSyncV2SnapshotPull(TerraScaleSyncV2PayloadCodec.LoroSnapshot1, [5], start);
        host.Script.DeltaAnswers.Enqueue(new TerraScaleSyncV2DeltaPull(TerraScaleSyncV2DeltaPullKind.Delta, TerraScaleSyncV2PayloadCodec.LoroDelta1, [0x11], next, 1, false, null));
        host.Script.DeltaAnswers.Enqueue(new TerraScaleSyncV2DeltaPull(TerraScaleSyncV2DeltaPullKind.UpToDate, TerraScaleSyncV2PayloadCodec.LoroDelta1, [], next, 0, false, null));
        using var http = CreateClient(host);
        await using var session = CreateSyncClient(http).CreateSession(new TerraScaleSyncV2SessionOptions("orders"));
        var updates = session.UpdatesAsync().GetAsyncEnumerator();
        await Assert.That(await updates.MoveNextAsync()).IsTrue();

        var firstUpdate = updates.MoveNextAsync().AsTask();
        _ = await host.NextRequestAsync();
        _ = await host.NextRequestAsync();
        _ = await host.NextRequestAsync();
        _ = await host.NextRequestAsync();
        var connection = await host.NextStreamConnectionAsync();

        // A malformed hint: still a pull (the pull is authoritative), never a fault.
        await connection.WriteRawAsync("id: 5\nevent: trigger\ndata: {not-json\n\n");
        await Assert.That(await firstUpdate).IsTrue();
        await Assert.That(updates.Current.Kind).IsEqualTo(TerraScaleSyncV2SyncUpdateKind.DeltaApplied);
        await Assert.That(session.State.TriggersObserved).IsEqualTo(0ul);

        // A well-formed hint afterwards still drives the loop.
        var secondUpdate = updates.MoveNextAsync().AsTask();
        await connection.TriggerAsync(6, "orders", 5, "op-5");
        await Assert.That(await secondUpdate).IsTrue();
        await Assert.That(session.State.TriggersObserved).IsEqualTo(1ul);
        await DisposeEnumeratorAsync(updates);
    }

    /// <summary>Multi-line data joins with a newline and strips at most one leading space (the SSE field-value rule).</summary>
    [Test]
    public async Task MultiLineDataJoinsWithNewlinesAndPreservesLeadingSpace()
    {
        using var host = await LoopbackSyncV2Host.StartAsync();
        var start = TerraScaleSyncV2Checkpoint.Create([(1ul, 6)], 3);
        var next = TerraScaleSyncV2Checkpoint.Create([(1ul, 8)], 4);
        host.Script.SetupAnswer = new TerraScaleSyncV2SessionAccepted(7ul, start, TerraScaleSyncV2Checkpoint.Initial);
        host.Script.SnapshotAnswer = new TerraScaleSyncV2SnapshotPull(TerraScaleSyncV2PayloadCodec.LoroSnapshot1, [5], start);
        host.Script.DeltaAnswers.Enqueue(new TerraScaleSyncV2DeltaPull(TerraScaleSyncV2DeltaPullKind.Delta, TerraScaleSyncV2PayloadCodec.LoroDelta1, [0x11], next, 1, false, null));
        host.Script.DeltaAnswers.Enqueue(new TerraScaleSyncV2DeltaPull(TerraScaleSyncV2DeltaPullKind.UpToDate, TerraScaleSyncV2PayloadCodec.LoroDelta1, [], next, 0, false, null));
        using var http = CreateClient(host);
        await using var session = CreateSyncClient(http).CreateSession(new TerraScaleSyncV2SessionOptions("orders"));
        var updates = session.UpdatesAsync().GetAsyncEnumerator();
        await Assert.That(await updates.MoveNextAsync()).IsTrue();

        var deltaUpdate = updates.MoveNextAsync().AsTask();
        _ = await host.NextRequestAsync();
        _ = await host.NextRequestAsync();
        _ = await host.NextRequestAsync();
        _ = await host.NextRequestAsync();
        var connection = await host.NextStreamConnectionAsync();

        // Two data lines whose spec join (newline, one leading space
        // stripped) is the JSON `{"collection":"a\n b",...}`.
        await connection.WriteRawAsync(
            "id: 7\nevent: trigger\ndata: {\"subscriptionId\":1,\"collection\":\"a\ndata:  b\",\"sequence\":4,\"operationId\":\"op-4\",\"kind\":\"trigger\"}\n\n");
        await Assert.That(await deltaUpdate).IsTrue();
        await Assert.That(session.State.TriggersObserved).IsEqualTo(1ul);
        await Assert.That(session.State.LastTrigger!.Value.Collection).IsEqualTo("a\n b");
        await DisposeEnumeratorAsync(updates);
    }

    /// <summary>An event beyond the stream-event bound ends the stream (bounded reading), never the loop.</summary>
    [Test]
    public async Task OversizeStreamEventsEndTheStreamNotTheLoop()
    {
        using var host = await LoopbackSyncV2Host.StartAsync();
        var start = TerraScaleSyncV2Checkpoint.Create([(1ul, 6)], 3);
        var next = TerraScaleSyncV2Checkpoint.Create([(1ul, 8)], 4);
        host.Script.SetupAnswer = new TerraScaleSyncV2SessionAccepted(7ul, start, TerraScaleSyncV2Checkpoint.Initial);
        host.Script.SnapshotAnswer = new TerraScaleSyncV2SnapshotPull(TerraScaleSyncV2PayloadCodec.LoroSnapshot1, [5], start);
        host.Script.DeltaAnswers.Enqueue(new TerraScaleSyncV2DeltaPull(TerraScaleSyncV2DeltaPullKind.Delta, TerraScaleSyncV2PayloadCodec.LoroDelta1, [0x11], next, 1, false, null));
        host.Script.DeltaAnswers.Enqueue(new TerraScaleSyncV2DeltaPull(TerraScaleSyncV2DeltaPullKind.UpToDate, TerraScaleSyncV2PayloadCodec.LoroDelta1, [], next, 0, false, null));
        using var http = CreateClient(host);
        await using var session = CreateSyncClient(http).CreateSession(new TerraScaleSyncV2SessionOptions("orders")
        {
            ReconnectDelay = TimeSpan.FromMilliseconds(30),
        });
        var updates = session.UpdatesAsync().GetAsyncEnumerator();
        await Assert.That(await updates.MoveNextAsync()).IsTrue();

        var deltaUpdate = updates.MoveNextAsync().AsTask();
        _ = await host.NextRequestAsync();
        _ = await host.NextRequestAsync();
        _ = await host.NextRequestAsync();
        _ = await host.NextRequestAsync();
        var first = await host.NextStreamConnectionAsync();

        // A well-formed but oversize trigger event (far past the default
        // one-megabyte event bound): the reader ends the stream and the
        // live loop reconnects instead of buffering or dispatching it.
        await first.WriteRawAsync(
            "id: 9\nevent: trigger\ndata: {\"subscriptionId\":1,\"collection\":\"" + new string('x', 2 * 1024 * 1024) + "\",\"sequence\":4,\"operationId\":\"op-4\",\"kind\":\"trigger\"}\n\n");
        var reconnect = await host.NextRequestAsync().WaitAsync(TimeSpan.FromSeconds(2));
        await Assert.That(reconnect.Path).IsEqualTo("/v1/streams");
        var second = await host.NextStreamConnectionAsync();
        await Assert.That(session.State.TriggersObserved).IsEqualTo(0ul);

        await second.TriggerAsync(5, "orders", 4, "op-4");
        await Assert.That(await deltaUpdate).IsTrue();
        await Assert.That(updates.Current.Kind).IsEqualTo(TerraScaleSyncV2SyncUpdateKind.DeltaApplied);
        await Assert.That(session.State.TriggersObserved).IsEqualTo(1ul);
        await DisposeEnumeratorAsync(updates);
    }

    /// <summary>A host body beyond the decode bound is a typed refusal, never an unbounded parse.</summary>
    [Test]
    public async Task OversizeHostBodiesAreRefusedTyped()
    {
        using var host = await LoopbackSyncV2Host.StartAsync();
        host.Script.SetupAnswer = new TerraScaleSyncV2SessionAccepted(7ul, TerraScaleSyncV2Checkpoint.Initial, TerraScaleSyncV2Checkpoint.Initial);
        host.Script.SnapshotBody = new byte[(32 * 1024 * 1024) + 1];
        using var http = CreateClient(host);
        await using var session = CreateSyncClient(http).CreateSession(new TerraScaleSyncV2SessionOptions("orders"));
        _ = await session.SetupAsync();
        _ = await host.NextRequestAsync();

        var failure = await Assert.ThrowsAsync<TerraScaleSyncV2HostException>(() => session.PullSnapshotAsync());
        _ = await host.NextRequestAsync();
        await Assert.That(failure!.Code).IsEqualTo("host-failure");
        await Assert.That(failure.Retryable).IsFalse();
    }

    /// <summary>A resync-required pull carries the floor the host stated in its 409 body.</summary>
    [Test]
    public async Task ResyncRequiredCarriesTheHostStatedFloor()
    {
        using var host = await LoopbackSyncV2Host.StartAsync();
        var floor = TerraScaleSyncV2Checkpoint.Create([(1ul, 30)], 20);
        host.Script.SetupAnswer = new TerraScaleSyncV2SessionAccepted(7ul, TerraScaleSyncV2Checkpoint.Initial, TerraScaleSyncV2Checkpoint.Initial);
        host.Script.DeltaAnswers.Enqueue(new TerraScaleSyncV2DeltaPull(
            TerraScaleSyncV2DeltaPullKind.ResyncRequired,
            TerraScaleSyncV2PayloadCodec.LoroDelta1,
            [],
            null!,
            0,
            false,
            floor));
        using var http = CreateClient(host);
        await using var session = CreateSyncClient(http).CreateSession(new TerraScaleSyncV2SessionOptions("orders"));
        _ = await session.SetupAsync();
        _ = await host.NextRequestAsync();

        var answer = await session.PullDeltaAsync();
        _ = await host.NextRequestAsync();

        await Assert.That(answer.Kind).IsEqualTo(TerraScaleSyncV2DeltaPullKind.ResyncRequired);
        await Assert.That(answer.Floor).IsEqualTo(floor);
    }

    /// <summary>A host that ends every re-opened session is given up on after bounded, backed-off re-opens.</summary>
    [Test]
    public async Task AnEndlesslyEndedSessionGivesUpAfterBoundedReopens()
    {
        using var host = await LoopbackSyncV2Host.StartAsync();
        var start = TerraScaleSyncV2Checkpoint.Create([(1ul, 6)], 3);
        host.Script.SetupAnswer = new TerraScaleSyncV2SessionAccepted(7ul, start, TerraScaleSyncV2Checkpoint.Initial);
        host.Script.EndEverySession = true;
        using var http = CreateClient(host);
        await using var session = CreateSyncClient(http).CreateSession(new TerraScaleSyncV2SessionOptions("orders")
        {
            Resume = start,
            ReconnectDelay = TimeSpan.FromMilliseconds(20),
        });
        var updates = session.UpdatesAsync().GetAsyncEnumerator();

        // The give-up is the typed host failure, not an unbounded re-open loop.
        var failure = await Assert.ThrowsAsync<TerraScaleSyncV2HostException>(
            () => updates.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
        await Assert.That(failure!.StatusCode).IsEqualTo(HttpStatusCode.Gone);

        // One initial setup plus at most the bounded re-opens.
        var requests = await host.DrainRequestsAsync();
        var setups = requests.Count(request => request.Path == "/v1/sync/sessions" && request.Method == "POST");
        await Assert.That(setups).IsLessThanOrEqualTo(4);
        await DisposeEnumeratorAsync(updates);
    }

    /// <summary>The local gate: missing backend-supplied context is a typed failure, not a silent fallback.</summary>
    [Test]
    public async Task MissingBackendContextFailsTheLocalGate()
    {
        using var host = await LoopbackSyncV2Host.StartAsync();
        using var http = CreateClient(host);
        _ = Assert.Throws<ArgumentException>(() => new TerraScaleSyncV2Client(http, new TerraScaleSyncV2ClientOptions()));

        host.Script.GateRefused = true;
        var client = CreateSyncClient(http);
        await using var session = client.CreateSession(new TerraScaleSyncV2SessionOptions("orders"));
        var failure = await Assert.ThrowsAsync<TerraScaleSyncV2HostException>(() => session.SetupAsync());
        await Assert.That(failure!.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(failure.Code).IsEqualTo("unauthorized");
        await Assert.That(failure.Retryable).IsFalse();
    }

    /// <summary>A checkpoint refusal without the resync marker is a typed conflict on the explicit pull path.</summary>
    [Test]
    public async Task ACheckpointRefusalWithoutResyncIsATypedConflict()
    {
        using var host = await LoopbackSyncV2Host.StartAsync();
        host.Script.SetupAnswer = new TerraScaleSyncV2SessionAccepted(7ul, TerraScaleSyncV2Checkpoint.Initial, TerraScaleSyncV2Checkpoint.Initial);
        host.Script.DeltaRefused = true;
        using var http = CreateClient(host);
        await using var session = CreateSyncClient(http).CreateSession(new TerraScaleSyncV2SessionOptions("orders"));
        _ = await session.SetupAsync();
        _ = await host.NextRequestAsync();

        var failure = await Assert.ThrowsAsync<TerraScaleSyncV2HostException>(() => session.PullDeltaAsync());
        _ = await host.NextRequestAsync();
        await Assert.That(failure!.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
        await Assert.That(failure.Code).IsEqualTo("checkpoint-refused");
        await Assert.That(failure.ResyncRequired).IsFalse();
        await Assert.That(failure.Retryable).IsFalse();
    }

    /// <summary>Disposal cancels the live enumeration and closes the stream.</summary>
    [Test]
    public async Task DisposalCancelsTheLiveEnumeration()
    {
        using var host = await LoopbackSyncV2Host.StartAsync();
        var start = TerraScaleSyncV2Checkpoint.Create([(1ul, 6)], 3);
        host.Script.SetupAnswer = new TerraScaleSyncV2SessionAccepted(7ul, start, TerraScaleSyncV2Checkpoint.Initial);
        host.Script.SnapshotAnswer = new TerraScaleSyncV2SnapshotPull(TerraScaleSyncV2PayloadCodec.LoroSnapshot1, [5], start);
        using var http = CreateClient(host);
        var session = CreateSyncClient(http).CreateSession(new TerraScaleSyncV2SessionOptions("orders"));
        var updates = session.UpdatesAsync().GetAsyncEnumerator();
        await Assert.That(await updates.MoveNextAsync()).IsTrue();
        var streaming = updates.MoveNextAsync().AsTask();
        _ = await host.NextRequestAsync();
        _ = await host.NextRequestAsync();
        _ = await host.NextRequestAsync();
        _ = await host.NextRequestAsync();
        _ = await host.NextStreamConnectionAsync();

        await session.DisposeAsync();
        _ = await Assert.ThrowsAsync<OperationCanceledException>(() => streaming);
        await DisposeEnumeratorAsync(updates);
    }

    /// <summary>The engine-unavailable surface is typed and retryable.</summary>
    [Test]
    public async Task AnUnavailableEngineIsTypedAndRetryable()
    {
        using var host = await LoopbackSyncV2Host.StartAsync();
        host.Script.Unavailable = true;
        using var http = CreateClient(host);
        await using var session = CreateSyncClient(http).CreateSession(new TerraScaleSyncV2SessionOptions("orders"));
        var failure = await Assert.ThrowsAsync<TerraScaleSyncV2HostException>(() => session.SetupAsync());
        await Assert.That(failure!.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        await Assert.That(failure.Code).IsEqualTo("engine-unavailable");
        await Assert.That(failure.Retryable).IsTrue();
    }

    /// <summary>Session options validate the collection identifier at construction.</summary>
    [Test]
    public async Task SessionOptionsValidateAtConstruction()
    {
        using var host = await LoopbackSyncV2Host.StartAsync();
        using var http = CreateClient(host);
        var client = CreateSyncClient(http);
        _ = Assert.Throws<ArgumentException>(() => client.CreateSession(new TerraScaleSyncV2SessionOptions("bad scope!")));
        _ = Assert.Throws<ArgumentException>(() => client.CreateSession(new TerraScaleSyncV2SessionOptions("")));
    }

    /// <summary>
    /// Disposes the live enumerator tolerantly: an async iterator that is
    /// still waiting on a pending <c>MoveNextAsync</c> (the live loop
    /// waiting for its next trigger) answers disposal with
    /// <see cref="NotSupportedException"/> until the pending move faults.
    /// </summary>
    private static async Task DisposeEnumeratorAsync(IAsyncEnumerator<TerraScaleSyncV2SyncUpdate> enumerator)
    {
        try
        {
            await enumerator.DisposeAsync();
        }
        catch (NotSupportedException)
        {
            // The enumerator was waiting for its next trigger.
        }
    }
}

/// <summary>One recorded request the loopback host observed.</summary>
public sealed record LoopbackSyncRequest(
    string Method,
    string Path,
    string Query,
    IReadOnlyDictionary<string, string> Headers,
    byte[] BodyBytes)
{
    /// <summary>A header value, or <c>null</c> when absent.</summary>
    public string? Header(string name) =>
        Headers.TryGetValue(name, out var value) ? value : null;

    /// <summary>The parsed JSON body.</summary>
    public JsonElement Json() => JsonDocument.Parse(Encoding.UTF8.GetString(BodyBytes)).RootElement;
}

/// <summary>The scripted mutation answer: the outcome the host returns.</summary>
/// <param name="Outcome">The applied receipt or typed refusal.</param>
public sealed record TerraScaleSyncV2MutationPull(TerraScaleSyncV2MutationOutcome Outcome);

/// <summary>The scripted answers of the loopback sync host.</summary>
public sealed class TerraScaleSyncV2Script
{
    /// <summary>The setup answer; a fresh session by default.</summary>
    public TerraScaleSyncV2SessionAccepted? SetupAnswer { get; set; }

    /// <summary>The snapshot answer.</summary>
    public TerraScaleSyncV2SnapshotPull? SnapshotAnswer { get; set; }

    /// <summary>The scripted delta answers, served in order.</summary>
    public Queue<TerraScaleSyncV2DeltaPull> DeltaAnswers { get; } = [];

    /// <summary>The scripted mutation answer.</summary>
    public TerraScaleSyncV2MutationPull? MutationAnswer { get; set; }

    /// <summary>Answer every delta with a typed conflict.</summary>
    public bool DeltaRefused { get; set; }

    /// <summary>Answer lifecycle frames with the ended-session status.</summary>
    public bool SessionEnded { get; set; }

    /// <summary>Answer with the local gate's unauthorized status.</summary>
    public bool GateRefused { get; set; }

    /// <summary>Answer with the engine-unavailable status.</summary>
    public bool Unavailable { get; set; }

    /// <summary>
    /// Refuse this many stream opens with <see cref="StreamRefusalStatus"/>
    /// (an engine blip) before serving the event stream.
    /// </summary>
    public int StreamRefusalsRemaining { get; set; }

    /// <summary>The status a refused stream open answers with. The default is 503.</summary>
    public string StreamRefusalStatus { get; set; } = "503 Service Unavailable";

    /// <summary>Answer every session-lifecycle request (below the setup path) with the ended-session status.</summary>
    public bool EndEverySession { get; set; }

    /// <summary>Serve these raw bytes as the snapshot answer body (a scripted oversize host body).</summary>
    public byte[]? SnapshotBody { get; set; }
}

/// <summary>A real loopback HTTP host serving the C# API host's documented sync endpoints from a test script.</summary>
public sealed class LoopbackSyncV2Host : IDisposable
{
    private readonly TcpListener listener;
    private readonly Channel<LoopbackSyncRequest> requests = Channel.CreateUnbounded<LoopbackSyncRequest>();
    private readonly Channel<StreamConnection> streams = Channel.CreateUnbounded<StreamConnection>();
    private readonly CancellationTokenSource stopping = new();
    private readonly Task acceptLoop;

    private LoopbackSyncV2Host(TcpListener listener)
    {
        this.listener = listener;
        this.Script = new TerraScaleSyncV2Script();
        this.acceptLoop = Task.Run(() => AcceptAsync());
    }

    /// <summary>The scripted answers.</summary>
    public TerraScaleSyncV2Script Script { get; }

    /// <summary>The host's loopback base address.</summary>
    public string BaseAddress => $"http://127.0.0.1:{((IPEndPoint)this.listener.LocalEndpoint).Port}";

    /// <summary>Starts the host on an ephemeral loopback port.</summary>
    public static async Task<LoopbackSyncV2Host> StartAsync()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start(16);
        return new LoopbackSyncV2Host(listener);
    }

    /// <summary>Whether any recorded request is still unread.</summary>
    public bool HasPendingRequests => this.requests.Reader.Count > 0;

    /// <summary>Waits for the next recorded request.</summary>
    public async Task<LoopbackSyncRequest> NextRequestAsync() =>
        await this.requests.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));

    /// <summary>Waits for the next opened stream connection.</summary>
    public async Task<StreamConnection> NextStreamConnectionAsync() =>
        await this.streams.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));

    /// <summary>Drains the recorded requests, waiting until quiet or the timeout elapses.</summary>
    public async Task<List<LoopbackSyncRequest>> DrainRequestsAsync(TimeSpan? quiet = null)
    {
        var window = quiet ?? TimeSpan.FromMilliseconds(250);
        var drained = new List<LoopbackSyncRequest>();
        using var timeout = new CancellationTokenSource(window);
        try
        {
            while (true)
            {
                var read = this.requests.Reader.ReadAsync(timeout.Token).AsTask();
                drained.Add(await read);
            }
        }
        catch (OperationCanceledException)
        {
            return drained;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        this.stopping.Cancel();
        this.listener.Stop();
        this.requests.Writer.TryComplete();
        this.streams.Writer.TryComplete();
        try
        {
            this.acceptLoop.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // The accept loop ends with the listener; nothing to observe.
        }
    }

    private async Task AcceptAsync()
    {
        while (!this.stopping.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await this.listener.AcceptTcpClientAsync(this.stopping.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (SocketException)
            {
                return;
            }

            _ = Task.Run(() => ServeAsync(client));
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        var stream = client.GetStream();
        try
        {
            while (!this.stopping.IsCancellationRequested)
            {
                var request = await ReadRequestAsync(stream);
                if (request is null)
                {
                    return;
                }

                await this.requests.Writer.WriteAsync(request);
                if (request.Path == "/v1/streams")
                {
                    if (this.Script.StreamRefusalsRemaining > 0)
                    {
                        // An engine blip: the stream open is refused with a
                        // retryable status (the SE-08 host answers 503 when
                        // the engine generation is down).
                        this.Script.StreamRefusalsRemaining--;
                        await WriteJsonAsync(
                            stream,
                            this.Script.StreamRefusalStatus,
                            new { code = "engine-unavailable", detail = "the engine is not serving" });
                        return;
                    }

                    // The stream connection stays open: its lifetime is
                    // the test's (it writes events and closes it). The
                    // response head carries no content length: the body
                    // is the unbounded event stream.
                    var connection = new StreamConnection(client, stream, this.stopping.Token);
                    var head = "HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nCache-Control: no-cache\r\nConnection: close\r\n\r\n";
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(head));
                    await stream.FlushAsync();
                    await this.streams.Writer.WriteAsync(connection);
                    await Task.Delay(Timeout.Infinite, this.stopping.Token);
                    return;
                }

                await AnswerAsync(stream, request);
                if (request.Header("Connection") == "close")
                {
                    return;
                }
            }
        }
        finally
        {
            client.Dispose();
        }
    }

    private async Task AnswerAsync(NetworkStream stream, LoopbackSyncRequest request)
    {
        if (this.Script.GateRefused)
        {
            await WriteJsonAsync(stream, "401 Unauthorized", new { code = "unauthorized", detail = "backend-supplied context (X-TB-Principal, X-TB-Tenant) is required" });
            return;
        }

        if (this.Script.Unavailable)
        {
            await WriteJsonAsync(stream, "503 Service Unavailable", new { code = "engine-unavailable", detail = "the engine is not serving" });
            return;
        }

        if (this.Script.SessionEnded && request.Path.StartsWith("/v1/sync/sessions/", StringComparison.Ordinal))
        {
            // One-shot: the ended session is answered as ended exactly
            // once; lifecycle on the newly opened session proceeds.
            this.Script.SessionEnded = false;
            await WriteJsonAsync(stream, "410 Gone", new { code = "session-ended", detail = "session is not live on this host; open a new session" });
            return;
        }

        if (this.Script.EndEverySession && request.Path.StartsWith("/v1/sync/sessions/", StringComparison.Ordinal))
        {
            // A host whose generation ends every session the client opens.
            await WriteJsonAsync(stream, "410 Gone", new { code = "session-ended", detail = "session is not live on this host; open a new session" });
            return;
        }

        if (request.Path == "/v1/sync/sessions" && request.Method == "POST")
        {
            var accepted = this.Script.SetupAnswer ?? new TerraScaleSyncV2SessionAccepted(
                7ul,
                TerraScaleSyncV2Checkpoint.Initial,
                TerraScaleSyncV2Checkpoint.Initial);
            await WriteJsonAsync(stream, "200 OK", new
            {
                sessionId = accepted.SessionId,
                checkpoint = Convert.ToBase64String(accepted.ServerCheckpoint.ToBytes()),
                retainedFloor = Convert.ToBase64String(accepted.RetainedFloor.ToBytes()),
            });
            return;
        }

        if (request.Path.EndsWith("/delta", StringComparison.Ordinal))
        {
            if (this.Script.DeltaRefused)
            {
                await WriteJsonAsync(stream, "409 Conflict", new { code = "checkpoint-refused", detail = "the checkpoint does not decode" });
                return;
            }

            if (this.Script.DeltaAnswers.Count == 0)
            {
                // An exhausted script serves the up-to-date answer: the
                // client's checkpoint echoed as the server's own.
                var from = Convert.FromBase64String(request.Json().GetProperty("from").GetString()!);
                await WriteJsonAsync(stream, "200 OK", new
                {
                    sessionId = SessionId(request.Path),
                    payloadCodec = "loro-delta/1",
                    payload = Convert.ToBase64String(Array.Empty<byte>()),
                    next = Convert.ToBase64String(from),
                    commits = 0u,
                    singleCommitOversize = false,
                });
                return;
            }

            var answer = this.Script.DeltaAnswers.Dequeue();

            if (answer.Kind == TerraScaleSyncV2DeltaPullKind.ResyncRequired)
            {
                await WriteJsonAsync(stream, "409 Conflict", new
                {
                    code = "checkpoint-refused",
                    detail = "behind-retained-floor",
                    resyncRequired = true,
                    floor = answer.Floor is null ? null : Convert.ToBase64String(answer.Floor.ToBytes()),
                });
                return;
            }

            await WriteJsonAsync(stream, "200 OK", new
            {
                sessionId = SessionId(request.Path),
                payloadCodec = "loro-delta/1",
                payload = Convert.ToBase64String(answer.Payload),
                next = Convert.ToBase64String(answer.Next!.ToBytes()),
                commits = answer.Commits,
                singleCommitOversize = answer.SingleCommitOversize,
            });
            return;
        }

        if (request.Path.EndsWith("/snapshot", StringComparison.Ordinal))
        {
            if (this.Script.SnapshotBody is { } rawBody)
            {
                await WriteResponseAsync(
                    stream,
                    "200 OK",
                    new Dictionary<string, string> { ["Content-Type"] = "application/json; charset=utf-8" },
                    rawBody);
                return;
            }

            var snapshot = this.Script.SnapshotAnswer ?? new TerraScaleSyncV2SnapshotPull(
                TerraScaleSyncV2PayloadCodec.LoroSnapshot1, [], TerraScaleSyncV2Checkpoint.Initial);
            await WriteJsonAsync(stream, "200 OK", new
            {
                sessionId = SessionId(request.Path),
                payloadCodec = "loro-snapshot/1",
                payload = Convert.ToBase64String(snapshot.Payload),
                checkpoint = Convert.ToBase64String(snapshot.Checkpoint.ToBytes()),
            });
            return;
        }

        if (request.Path.EndsWith("/ack", StringComparison.Ordinal))
        {
            await WriteJsonAsync(stream, "200 OK", new { sessionId = SessionId(request.Path) });
            return;
        }

        if (request.Path.EndsWith("/mutations", StringComparison.Ordinal))
        {
            var mutation = this.Script.MutationAnswer ?? new TerraScaleSyncV2MutationPull(
                new TerraScaleSyncV2MutationApplied(1ul, "op"));
            var outcome = mutation.Outcome switch
            {
                TerraScaleSyncV2MutationApplied applied => (object)new
                {
                    outcome = "applied",
                    receipt = new { sequence = applied.Sequence, version = 4ul, operationId = applied.OperationId },
                },
                TerraScaleSyncV2MutationRejected rejected => new { outcome = "rejected", reason = rejected.WireName },
                _ => throw new InvalidOperationException("unknown mutation outcome"),
            };
            await WriteJsonAsync(stream, "200 OK", new { sessionId = SessionId(request.Path), outcome });
            return;
        }

        if (request.Path.EndsWith("/close", StringComparison.Ordinal))
        {
            await WriteJsonAsync(stream, "200 OK", new { sessionId = SessionId(request.Path) });
            return;
        }

        await WriteJsonAsync(stream, "404 Not Found", new { code = "not-found", detail = request.Path });
    }

    private static ulong SessionId(string path)
    {
        var segment = path.Split('/')[4];
        return ulong.Parse(segment, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<LoopbackSyncRequest?> ReadRequestAsync(NetworkStream stream)
    {
        var buffer = new byte[64 * 1024];
        var headerEnd = -1;
        var length = 0;
        while (headerEnd < 0)
        {
            if (length == buffer.Length)
            {
                return null;
            }

            var read = await stream.ReadAsync(buffer.AsMemory(length));
            if (read == 0)
            {
                return null;
            }

            length += read;
            headerEnd = Encoding.ASCII.GetString(buffer, 0, length).IndexOf("\r\n\r\n", StringComparison.Ordinal);
        }

        var headerText = Encoding.ASCII.GetString(buffer, 0, headerEnd);
        var lines = headerText.Split("\r\n");
        var parts = lines[0].Split(' ');
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            var separator = line.IndexOf(':');
            if (separator > 0)
            {
                headers[line[..separator].Trim()] = line[(separator + 1)..].Trim();
            }
        }

        var bodyLength = headers.TryGetValue("Content-Length", out var declared) ? int.Parse(declared, System.Globalization.CultureInfo.InvariantCulture) : 0;
        var bodyStart = headerEnd + 4;
        var body = new byte[bodyLength];
        var have = length - bodyStart;
        if (have > 0)
        {
            Array.Copy(buffer, bodyStart, body, 0, Math.Min(have, bodyLength));
        }

        while (have < bodyLength)
        {
            var read = await stream.ReadAsync(body.AsMemory(have));
            if (read == 0)
            {
                return null;
            }

            have += read;
        }

        var queryIndex = parts[1].IndexOf('?');
        return new LoopbackSyncRequest(
            parts[0],
            queryIndex >= 0 ? parts[1][..queryIndex] : parts[1],
            queryIndex >= 0 ? parts[1][(queryIndex + 1)..] : string.Empty,
            headers,
            body);
    }

    private static async Task WriteJsonAsync(NetworkStream stream, string status, object body)
    {
        var json = JsonSerializer.Serialize(body);
        await WriteResponseAsync(
            stream,
            status,
            new Dictionary<string, string>
            {
                ["Content-Type"] = "application/json; charset=utf-8",
                ["Connection"] = "close",
            },
            Encoding.UTF8.GetBytes(json));
    }

    private static async Task WriteResponseAsync(
        NetworkStream stream,
        string status,
        IReadOnlyDictionary<string, string> headers,
        byte[] body)
    {
        var head = new StringBuilder($"HTTP/1.1 {status}\r\n");
        foreach (var header in headers)
        {
            head.Append($"{header.Key}: {header.Value}\r\n");
        }

        head.Append($"Content-Length: {body.Length}\r\n\r\n");
        var headBytes = Encoding.ASCII.GetBytes(head.ToString());
        await stream.WriteAsync(headBytes);
        await stream.WriteAsync(body);
        await stream.FlushAsync();
    }

    /// <summary>One open server-sent-event connection the test writes to.</summary>
    public sealed class StreamConnection
    {
        private readonly TcpClient client;
        private readonly NetworkStream stream;
        private readonly CancellationToken stopped;

        internal StreamConnection(TcpClient client, NetworkStream stream, CancellationToken stopped)
        {
            this.client = client;
            this.stream = stream;
            this.stopped = stopped;
        }

        /// <summary>Writes one trigger notification event.</summary>
        public async Task TriggerAsync(ulong id, string collection, ulong sequence, string operationId)
        {
            var payload = JsonSerializer.Serialize(new
            {
                subscriptionId = 1ul,
                collection,
                sequence,
                operationId,
                kind = "trigger",
            });
            await WriteEventAsync(id, "trigger", payload);
        }

        /// <summary>Writes one generic event.</summary>
        public async Task WriteEventAsync(ulong id, string name, string payload)
        {
            if (this.stopped.IsCancellationRequested)
            {
                return;
            }

            var text = $"id: {id}\nevent: {name}\ndata: {payload}\n\n";
            var bytes = Encoding.UTF8.GetBytes(text);
            await this.stream.WriteAsync(bytes, this.stopped);
            await this.stream.FlushAsync(this.stopped);
        }

        /// <summary>Writes raw event-stream text (malformed or hand-shaped events).</summary>
        public async Task WriteRawAsync(string text)
        {
            if (this.stopped.IsCancellationRequested)
            {
                return;
            }

            var bytes = Encoding.UTF8.GetBytes(text);
            await this.stream.WriteAsync(bytes, this.stopped);
            await this.stream.FlushAsync(this.stopped);
        }

        /// <summary>Closes the connection, simulating a dropped stream.</summary>
        public void Close()
        {
            try
            {
                this.stream.Socket?.Shutdown(SocketShutdown.Both);
            }
            catch (SocketException)
            {
                // Already closed.
            }

            this.stream.Socket?.Close();
        }
    }
}

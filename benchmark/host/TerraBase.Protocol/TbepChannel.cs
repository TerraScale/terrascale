// <copyright file="TbepChannel.cs" company="TerraScale">
// TerraBaseV2 (SE-08). See LICENSE.md.
// </copyright>

namespace TerraBase.Protocol;

using System.Linq;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Channels;

/// <summary>
/// The serving half of the TBEP client (SE-08): one engine connection
/// multiplexed between concurrent typed calls and the unsolicited
/// backbone notifications. The <see cref="TbepClient"/> stays the only
/// socket path — this channel takes over its transport and runs one
/// reader that demultiplexes every inbound frame: an answer completes
/// its pending call, a notification (correlation 0, causation naming its
/// subscription) is routed to the registered sink, in arrival order.
/// </summary>
public sealed class TbepChannel : IAsyncDisposable
{
    private readonly TbepClient client;
    private readonly NetworkStream stream;
    private readonly SemaphoreSlim writeGate = new(1, 1);
    private readonly object gate = new();
    private readonly Dictionary<ulong, TaskCompletionSource<TbepWire.Frame>> pending = [];
    private readonly Dictionary<ulong, ITbepNotificationSink> sinks = [];
    private readonly CancellationTokenSource disposed = new();
    private readonly Task reading;
    private ulong correlation;
    private bool closed;

    private TbepChannel(TbepClient client)
    {
        this.client = client;
        this.stream = client.Transport;
        this.reading = Task.Run(this.ReadLoopAsync);
    }

    /// <summary>The host half of a notification stream: one subscription's inbound frames.</summary>
    public interface ITbepNotificationSink
    {
        /// <summary>One ordered batch of change events.</summary>
        /// <param name="batch">The batch.</param>
        void OnBatch(SubscriptionBatch batch);

        /// <summary>The content-free trigger (PD-2026-08-26-13 D3).</summary>
        /// <param name="trigger">The trigger.</param>
        void OnTrigger(TriggerNotification trigger);

        /// <summary>The subscription closed.</summary>
        /// <param name="closed">The close frame.</param>
        void OnClosed(SubscriptionClosed closed);
    }

    /// <summary>Connects to a serving engine process at a UDS path.</summary>
    /// <param name="path">The socket path the engine bound.</param>
    /// <returns>The channel, unbound and not yet serving tenant-scoped frames.</returns>
    public static TbepChannel Connect(string path) => new(TbepClient.Connect(path));

    /// <summary>The bound tenant, or <c>null</c> before the channel binding.</summary>
    public string? Tenant => this.client.Tenant;

    /// <summary>Answers whether the channel is closed: the engine ended it, or it was disposed.</summary>
    public bool IsClosed => Volatile.Read(ref this.closed);

    /// <summary>Binds the channel to the backend-supplied principal and tenant (PD-2026-08-26-11 D1).</summary>
    /// <param name="principal">The backend principal.</param>
    /// <param name="tenant">The backend-supplied tenant the engine enforces.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The bind response.</returns>
    /// <exception cref="TbepException">The engine refused the binding or the channel failed.</exception>
    public async Task<ChannelBindResponse> BindChannelAsync(string principal, string tenant, CancellationToken cancellationToken = default)
    {
        var response = await this.CallAsync<ChannelBindRequest, ChannelBindResponse>(
            "channel.bind",
            new ChannelBindRequest { Principal = principal, Tenant = tenant },
            idempotency: null,
            cancellationToken).ConfigureAwait(false);
        this.client.AdoptTenant(tenant);
        return response;
    }

    /// <summary>Serves one typed document read.</summary>
    /// <param name="collection">The collection.</param>
    /// <param name="document">The document id.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The read response.</returns>
    /// <exception cref="TbepException">The operation failed.</exception>
    public Task<DocumentReadResponse> DocumentReadAsync(string collection, string document, CancellationToken cancellationToken = default) =>
        this.CallAsync<DocumentReadRequest, DocumentReadResponse>(
            "document.read",
            new DocumentReadRequest { Collection = collection, Document = document },
            idempotency: null,
            cancellationToken);

    /// <summary>Serves one typed document write (mutation; the idempotency key is the retry identity).</summary>
    /// <param name="document">The document put.</param>
    /// <param name="expected">The expected-version conditional.</param>
    /// <param name="operationId">The operation id.</param>
    /// <param name="idempotencyKey">The retry identity.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The write response.</returns>
    /// <exception cref="TbepException">The operation failed.</exception>
    public Task<DocumentWriteResponse> DocumentWriteAsync(DocumentPut document, ExpectedVersion expected, string operationId, string idempotencyKey, CancellationToken cancellationToken = default) =>
        this.CallAsync<DocumentWriteRequest, DocumentWriteResponse>(
            "document.write",
            new DocumentWriteRequest { Document = document, Expected = expected, OperationId = operationId },
            idempotency: idempotencyKey,
            cancellationToken);

    /// <summary>Serves one typed document delete (mutation).</summary>
    /// <param name="collection">The collection.</param>
    /// <param name="document">The document id.</param>
    /// <param name="expected">The expected-version conditional.</param>
    /// <param name="operationId">The operation id.</param>
    /// <param name="idempotencyKey">The retry identity.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The delete response.</returns>
    /// <exception cref="TbepException">The operation failed.</exception>
    public Task<DocumentDeleteResponse> DocumentDeleteAsync(string collection, string document, ExpectedVersion expected, string operationId, string idempotencyKey, CancellationToken cancellationToken = default) =>
        this.CallAsync<DocumentDeleteRequest, DocumentDeleteResponse>(
            "document.delete",
            new DocumentDeleteRequest { Collection = collection, Document = document, Expected = expected, OperationId = operationId },
            idempotency: idempotencyKey,
            cancellationToken);

    /// <summary>Serves one typed query execution.</summary>
    /// <param name="queryId">The caller-assigned query identity.</param>
    /// <param name="text">The query text.</param>
    /// <param name="maxItems">The item bound.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The query response.</returns>
    /// <exception cref="TbepException">The operation failed.</exception>
    public Task<QueryExecuteResponse> QueryExecuteAsync(ulong queryId, string text, uint maxItems, CancellationToken cancellationToken = default) =>
        this.CallAsync<QueryExecuteRequest, QueryExecuteResponse>(
            "query.execute",
            new QueryExecuteRequest { QueryId = queryId, Text = text, MaxItems = maxItems },
            idempotency: null,
            cancellationToken);

    /// <summary>Sends the query-cancellation live signal (GAP-Q12, PB-4034).</summary>
    /// <param name="queryId">The in-flight query identity.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The cancel response.</returns>
    /// <exception cref="TbepException">The operation failed.</exception>
    public Task<QueryCancelResponse> QueryCancelAsync(ulong queryId, CancellationToken cancellationToken = default) =>
        this.CallAsync<QueryCancelRequest, QueryCancelResponse>(
            "query.cancel",
            new QueryCancelRequest { QueryId = queryId },
            idempotency: null,
            cancellationToken);

    /// <summary>Serves one typed family read.</summary>
    /// <param name="request">The family request.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The family response.</returns>
    /// <exception cref="TbepException">The operation failed.</exception>
    public Task<FamilyReadResponse> FamilyReadAsync(FamilyReadRequest request, CancellationToken cancellationToken = default) =>
        this.CallAsync<FamilyReadRequest, FamilyReadResponse>("family.read", request, idempotency: null, cancellationToken);

    /// <summary>Serves one typed family write (mutation).</summary>
    /// <param name="request">The family request.</param>
    /// <param name="idempotencyKey">The retry identity.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The family response.</returns>
    /// <exception cref="TbepException">The operation failed.</exception>
    public Task<FamilyWriteResponse> FamilyWriteAsync(FamilyWriteRequest request, string idempotencyKey, CancellationToken cancellationToken = default) =>
        this.CallAsync<FamilyWriteRequest, FamilyWriteResponse>("family.write", request, idempotency: idempotencyKey, cancellationToken);

    /// <summary>Subscribes one backbone stream and registers its notification sink.</summary>
    /// <param name="subscriptionId">The caller-assigned subscription identity.</param>
    /// <param name="collections">The collection filter, or <c>null</c> for every collection.</param>
    /// <param name="fromSequence">The replay start, or <c>null</c> for live-only.</param>
    /// <param name="sink">The sink that receives this subscription's frames.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The subscribe response.</returns>
    /// <exception cref="TbepException">The operation failed.</exception>
    public async Task<BackboneSubscribeResponse> BackboneSubscribeAsync(
        ulong subscriptionId,
        string[]? collections,
        ulong? fromSequence,
        ITbepNotificationSink sink,
        CancellationToken cancellationToken = default)
    {
        var from = fromSequence is { } sequence
            ? SubscriptionStart.Sequence(sequence)
            : SubscriptionStart.Live();
        // The sink registers before the request is sent: the engine may
        // stream the subscription's first (replayed) batch before this
        // call's continuation runs, and the reader loop must find the
        // sink waiting — never drop the first batch of a replay.
        lock (this.gate)
        {
            if (this.closed)
            {
                throw new TbepException("the engine channel is closed; backbone.subscribe was not sent");
            }

            this.sinks[subscriptionId] = sink;
        }

        try
        {
            return await this.CallAsync<BackboneSubscribeRequest, BackboneSubscribeResponse>(
                "backbone.subscribe",
                new BackboneSubscribeRequest { SubscriptionId = subscriptionId, Collections = collections, From = from },
                idempotency: null,
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            lock (this.gate)
            {
                this.sinks.Remove(subscriptionId);
            }

            throw;
        }
    }

    /// <summary>Acknowledges a subscription through a sequence.</summary>
    /// <param name="subscriptionId">The subscription.</param>
    /// <param name="throughSequence">The last sequence the consumer held.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The acknowledge response.</returns>
    /// <exception cref="TbepException">The operation failed.</exception>
    public Task<BackboneAcknowledgeResponse> BackboneAcknowledgeAsync(ulong subscriptionId, ulong throughSequence, CancellationToken cancellationToken = default) =>
        this.CallAsync<BackboneAcknowledgeRequest, BackboneAcknowledgeResponse>(
            "backbone.acknowledge",
            new BackboneAcknowledgeRequest { SubscriptionId = subscriptionId, ThroughSequence = throughSequence },
            idempotency: null,
            cancellationToken);

    /// <summary>Unsubscribes a stream and drops its sink.</summary>
    /// <param name="subscriptionId">The subscription.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The unsubscribe response.</returns>
    /// <exception cref="TbepException">The operation failed.</exception>
    public async Task<BackboneUnsubscribeResponse> BackboneUnsubscribeAsync(ulong subscriptionId, CancellationToken cancellationToken = default)
    {
        var response = await this.CallAsync<BackboneUnsubscribeRequest, BackboneUnsubscribeResponse>(
            "backbone.unsubscribe",
            new BackboneUnsubscribeRequest { SubscriptionId = subscriptionId },
            idempotency: null,
            cancellationToken).ConfigureAwait(false);
        lock (this.gate)
        {
            this.sinks.Remove(subscriptionId);
        }

        return response;
    }

    /// <summary>Opens one sync session on a collection scope.</summary>
    /// <param name="collection">The collection scope.</param>
    /// <param name="resume">The resume checkpoint bytes, or <c>null</c> for a fresh session.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The session setup response.</returns>
    /// <exception cref="TbepException">The operation failed.</exception>
    public Task<SyncSessionSetupResponse> SyncSessionSetupAsync(string collection, byte[]? resume, CancellationToken cancellationToken = default) =>
        this.CallAsync<SyncSessionSetupRequest, SyncSessionSetupResponse>(
            "sync.session_setup",
            new SyncSessionSetupRequest { Collection = collection, Resume = resume },
            idempotency: null,
            cancellationToken);

    /// <summary>Pulls one budgeted delta batch after a checkpoint.</summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="from">The checkpoint bytes to serve after.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The delta response.</returns>
    /// <exception cref="TbepException">The operation failed.</exception>
    public Task<SyncDeltaPullResponse> SyncDeltaPullAsync(ulong sessionId, byte[] from, CancellationToken cancellationToken = default) =>
        this.CallAsync<SyncDeltaPullRequest, SyncDeltaPullResponse>(
            "sync.delta_pull",
            new SyncDeltaPullRequest { SessionId = sessionId, From = from },
            idempotency: null,
            cancellationToken);

    /// <summary>Pulls one full snapshot.</summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The snapshot response.</returns>
    /// <exception cref="TbepException">The operation failed.</exception>
    public Task<SyncSnapshotPullResponse> SyncSnapshotPullAsync(ulong sessionId, CancellationToken cancellationToken = default) =>
        this.CallAsync<SyncSnapshotPullRequest, SyncSnapshotPullResponse>(
            "sync.snapshot_pull",
            new SyncSnapshotPullRequest { SessionId = sessionId },
            idempotency: null,
            cancellationToken);

    /// <summary>Acknowledges a session's durable checkpoint.</summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="checkpoint">The checkpoint bytes.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The ack response.</param>
    /// <exception cref="TbepException">The operation failed.</exception>
    public Task<SyncCheckpointAckResponse> SyncCheckpointAckAsync(ulong sessionId, byte[] checkpoint, CancellationToken cancellationToken = default) =>
        this.CallAsync<SyncCheckpointAckRequest, SyncCheckpointAckResponse>(
            "sync.checkpoint_ack",
            new SyncCheckpointAckRequest { SessionId = sessionId, Checkpoint = checkpoint },
            idempotency: null,
            cancellationToken);

    /// <summary>Submits one server-ack mutation; the idempotency key is the at-least-once retry identity.</summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="document">The document id.</param>
    /// <param name="change">The change.</param>
    /// <param name="idempotencyKey">The retry identity.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The mutation outcome.</returns>
    /// <exception cref="TbepException">The operation failed.</exception>
    public Task<SyncMutationSubmitResponse> SyncMutationSubmitAsync(ulong sessionId, string document, SyncChange change, string idempotencyKey, CancellationToken cancellationToken = default) =>
        this.CallAsync<SyncMutationSubmitRequest, SyncMutationSubmitResponse>(
            "sync.mutation_submit",
            new SyncMutationSubmitRequest { SessionId = sessionId, Document = document, Change = change },
            idempotency: idempotencyKey,
            cancellationToken);

    /// <summary>Closes one sync session.</summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The close response.</returns>
    /// <exception cref="TbepException">The operation failed.</exception>
    public Task<SyncSessionCloseResponse> SyncSessionCloseAsync(ulong sessionId, CancellationToken cancellationToken = default) =>
        this.CallAsync<SyncSessionCloseRequest, SyncSessionCloseResponse>(
            "sync.session_close",
            new SyncSessionCloseRequest { SessionId = sessionId },
            idempotency: null,
            cancellationToken);

    /// <summary>Reads the serving topology.</summary>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The topology response.</returns>
    /// <exception cref="TbepException">The operation failed.</exception>
    public Task<TopologyReadResponse> TopologyReadAsync(CancellationToken cancellationToken = default) =>
        this.CallAsync<TopologyReadRequest, TopologyReadResponse>(
            "topology.read",
            new TopologyReadRequest(),
            idempotency: null,
            cancellationToken);

    /// <summary>Reads the engine's serving counters.</summary>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The status response.</returns>
    /// <exception cref="TbepException">The operation failed.</exception>
    public Task<AdminStatusResponse> AdminStatusAsync(CancellationToken cancellationToken = default) =>
        this.CallAsync<AdminStatusRequest, AdminStatusResponse>(
            "admin.status",
            new AdminStatusRequest(),
            idempotency: null,
            cancellationToken);

    /// <summary>Reads the engine's supervision state.</summary>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The health response.</returns>
    /// <exception cref="TbepException">The operation failed.</exception>
    public Task<SupervisionHealthResponse> SupervisionHealthAsync(CancellationToken cancellationToken = default) =>
        this.CallAsync<SupervisionHealthRequest, SupervisionHealthResponse>(
            "supervision.health",
            new SupervisionHealthRequest(),
            idempotency: null,
            cancellationToken);

    /// <summary>Begins the engine's graceful shutdown.</summary>
    /// <param name="reason">The shutdown reason.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The shutdown response.</returns>
    /// <exception cref="TbepException">The operation failed.</exception>
    public Task<SupervisionShutdownResponse> SupervisionShutdownAsync(ShutdownReason reason, CancellationToken cancellationToken = default) =>
        this.CallAsync<SupervisionShutdownRequest, SupervisionShutdownResponse>(
            "supervision.shutdown",
            new SupervisionShutdownRequest { Reason = reason },
            idempotency: null,
            cancellationToken);

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        this.MarkClosed();
        this.disposed.Cancel();
        await this.client.DisposeAsync().ConfigureAwait(false);
        await this.reading.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        this.writeGate.Dispose();
        this.disposed.Dispose();
    }

    private async Task<TResponse> CallAsync<TRequest, TResponse>(
        string operation,
        TRequest request,
        string? idempotency,
        CancellationToken cancellationToken)
        where TRequest : notnull
        where TResponse : notnull
    {
        if (this.IsClosed)
        {
            throw new TbepException($"the engine channel is closed; {operation} was not sent");
        }

        var body = MessagePackSerializer.Serialize(request, TbepOptions.Strict);
        var completion = new TaskCompletionSource<TbepWire.Frame>(TaskCreationOptions.RunContinuationsAsynchronously);
        ulong correlation;
        await this.writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            correlation = ++this.correlation;
            lock (this.gate)
            {
                if (this.closed)
                {
                    throw new TbepException($"the engine channel is closed; {operation} was not sent");
                }

                this.pending[correlation] = completion;
            }

            var frame = TbepWire.Seal(TbepRegistry.RequestKindOf(operation), correlation, 0, idempotency, body);
            await TbepWire.WriteFrame(this.stream, frame).ConfigureAwait(false);
        }
        finally
        {
            this.writeGate.Release();
        }

        using var link = cancellationToken.Register(() => this.Abandon(correlation, completion));
        var answer = await completion.Task.ConfigureAwait(false);
        if (answer.Kind == TbepRegistry.KindCode("Error"))
        {
            var error = MessagePackSerializer.Deserialize<Error>(answer.Body, TbepOptions.Strict);
            throw new TbepException(operation, error.Code, error.Detail);
        }

        return MessagePackSerializer.Deserialize<TResponse>(answer.Body, TbepOptions.Strict);
    }

    private void Abandon(ulong correlation, TaskCompletionSource<TbepWire.Frame> completion)
    {
        lock (this.gate)
        {
            this.pending.Remove(correlation);
        }

        completion.TrySetCanceled();
    }

    private async Task ReadLoopAsync()
    {
        while (!this.disposed.IsCancellationRequested)
        {
            TbepWire.Frame? frame;
            try
            {
                frame = await TbepWire.ReadFrame(this.stream).ConfigureAwait(false);
            }
            catch (Exception failure) when (failure is IOException or SocketException or ObjectDisposedException or TbepWire.WireException)
            {
                this.FailPending(new TbepException($"the engine channel failed: {failure.Message}"));
                this.MarkClosed();
                return;
            }

            if (frame is null)
            {
                this.FailPending(new TbepException("the engine closed the channel"));
                this.MarkClosed();
                return;
            }

            this.Dispatch(frame);
        }
    }

    private void Dispatch(TbepWire.Frame frame)
    {
        if (frame.Correlation == 0)
        {
            ITbepNotificationSink? sink;
            lock (this.gate)
            {
                this.sinks.TryGetValue(frame.Causation, out sink);
            }

            if (sink is null)
            {
                return;
            }

            switch (frame.Kind)
            {
                case (ushort)TbepFrameKind.SubscriptionBatch:
                    sink.OnBatch(MessagePackSerializer.Deserialize<SubscriptionBatch>(frame.Body, TbepOptions.Strict));
                    break;
                case (ushort)TbepFrameKind.TriggerNotification:
                    sink.OnTrigger(MessagePackSerializer.Deserialize<TriggerNotification>(frame.Body, TbepOptions.Strict));
                    break;
                case (ushort)TbepFrameKind.SubscriptionClosed:
                    sink.OnClosed(MessagePackSerializer.Deserialize<SubscriptionClosed>(frame.Body, TbepOptions.Strict));
                    break;
                default:
                    return;
            }

            return;
        }

        TaskCompletionSource<TbepWire.Frame>? completion;
        lock (this.gate)
        {
            this.pending.Remove(frame.Causation, out completion);
        }

        if (completion is not null)
        {
            completion.TrySetResult(frame);
            return;
        }

        // A decode-level refusal: the engine could not parse the frame,
        // so its error frame names no causation (the refused frame's
        // identity is unreadable by construction). The engine processes
        // a connection's frames in arrival order and answers in that
        // order, so the oldest still-pending call is the refused one —
        // the refusal surfaces to that caller as its typed error, never
        // as a hang; the connection stays usable for the next frame.
        if (frame.Kind == TbepRegistry.KindCode("Error"))
        {
            TaskCompletionSource<TbepWire.Frame>? refused = null;
            lock (this.gate)
            {
                foreach (var correlation in this.pending.Keys.OrderBy(key => key))
                {
                    if (this.pending.Remove(correlation, out refused))
                    {
                        break;
                    }
                }
            }

            refused?.TrySetResult(frame);
        }
    }

    private void FailPending(TbepException failure)
    {
        List<TaskCompletionSource<TbepWire.Frame>> waiting;
        lock (this.gate)
        {
            waiting = [.. this.pending.Values];
            this.pending.Clear();
        }

        foreach (var completion in waiting)
        {
            completion.TrySetException(failure);
        }
    }

    private void MarkClosed()
    {
        lock (this.gate)
        {
            this.closed = true;
        }
    }
}

/// <summary>The host-side additions to the SE-14 client the serving channel builds on.</summary>
public sealed partial class TbepClient
{
    /// <summary>The channel's transport: the stream of the socket this client opened.</summary>
    internal NetworkStream Transport => this.stream;

    /// <summary>Records the tenant a serving channel bound (the channel, not the caller, owns the binding).</summary>
    /// <param name="tenant">The bound tenant.</param>
    internal void AdoptTenant(string tenant) => this.tenant = tenant;
}

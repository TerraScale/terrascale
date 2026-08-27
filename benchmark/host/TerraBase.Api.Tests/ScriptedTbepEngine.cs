// <copyright file="ScriptedTbepEngine.cs" company="TerraScale">
// TerraBaseV2 (SE-08). See LICENSE.md.
// </copyright>

namespace TerraBase.Api.Tests;

using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text;

/// <summary>
/// The scripted engine fixture of the host test family: a real UDS
/// server speaking TBEP over the SE-14 codec, mirroring the semantics
/// of the Rust <c>SyncDeliveryHost</c> and <c>TbepServer</c> the
/// hermetic rows depend on — the channel binding (one bind per
/// connection, refused while draining), the engine-enforced tenant
/// isolation over the session and subscription lifecycle (the typed
/// <c>bound to another channel</c> refusals), the backbone subscription
/// streams with ordered batches and content-free triggers, the sync
/// session lifecycle, and the supervision contract. Document, query,
/// and family frames answer scripted outcomes (a fixture, not an
/// engine); the real-engine rows run against the Rust process.
/// </summary>
public sealed class ScriptedTbepEngine : IAsyncDisposable
{
    private readonly Socket listener = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
    private readonly CancellationTokenSource stopped = new();
    private readonly Task serving;
    private readonly ConcurrentDictionary<ulong, string> sessionTenants = new();
    private readonly ConcurrentDictionary<ulong, string> subscriptionTenants = new();
    private readonly ConcurrentDictionary<string, List<ChangeEvent>> journals = new();
    private readonly ConcurrentDictionary<string, ulong> nextSequences = new();
    private readonly object idGate = new();
    private ulong nextSession;

    /// <summary>Initializes the scripted engine at a UDS path.</summary>
    /// <param name="path">The socket path.</param>
    public ScriptedTbepEngine(string path)
    {
        this.Path = path;
        this.listener.Bind(new UnixDomainSocketEndPoint(path));
        this.listener.Listen(8);
        this.serving = this.AcceptAsync(this.stopped.Token);
    }

    /// <summary>The bound socket path.</summary>
    public string Path { get; }

    /// <summary>Every request kind the engine received, with its bound tenant (the gate proof: refused callers never appear).</summary>
    public ConcurrentQueue<string> Received { get; } = [];

    /// <summary>The scripted document read answers, keyed by collection/document.</summary>
    public ConcurrentDictionary<string, DocumentBody> Documents { get; } = new();

    /// <summary>When set, the engine refuses every new binding (a draining engine).</summary>
    public bool Draining { get; set; }

    private async Task AcceptAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            Socket socket;
            try
            {
                socket = await this.listener.AcceptAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (SocketException)
            {
                return;
            }

            _ = Task.Run(() => this.ServeAsync(socket, cancellationToken), cancellationToken);
        }
    }

    private async Task ServeAsync(Socket socket, CancellationToken cancellationToken)
    {
        await using var stream = new NetworkStream(socket, ownsSocket: true);
        using var writeGate = new SemaphoreSlim(1, 1);
        var correlation = 0ul;
        var tenant = default(string?);
        var subscriptions = new List<Subscription>();
        while (!cancellationToken.IsCancellationRequested)
        {
            TbepWire.Frame? request;
            try
            {
                request = await TbepWire.ReadFrame(stream).ConfigureAwait(false);
            }
            catch (Exception failure) when (failure is IOException or ObjectDisposedException or TbepWire.WireException)
            {
                return;
            }

            if (request is null)
            {
                return;
            }

            correlation++;
            var kind = TbepRegistry.KindName(request.Kind);
            this.Received.Enqueue(tenant is null ? $"unbound:{kind}" : $"{tenant}:{kind}");

            // The engine-enforced tenant scoping (PD-2026-08-26-11 D1):
            // every tenant-scoped frame requires the channel binding.
            var scoped = kind is not ("ChannelBindRequest" or "SupervisionHealthRequest" or "SupervisionShutdownRequest");
            if (scoped && tenant is null)
            {
                await this.ErrorAsync(stream, writeGate, request.Correlation, ErrorCode.UnboundChannel(), "bind the channel first").ConfigureAwait(false);
                continue;
            }

            switch (kind)
            {
                case "ChannelBindRequest":
                {
                    var body = MessagePackSerializer.Deserialize<ChannelBindRequest>(request.Body, TbepOptions.Strict);
                    if (tenant is not null)
                    {
                        await this.ErrorAsync(stream, writeGate, request.Correlation, ErrorCode.AlreadyBound(), "channel bound").ConfigureAwait(false);
                        continue;
                    }

                    if (this.Draining)
                    {
                        await this.ErrorAsync(stream, writeGate, request.Correlation, ErrorCode.OperationRefused(), "the host is draining").ConfigureAwait(false);
                        continue;
                    }

                    tenant = body.Tenant;
                    await this.AnswerAsync(
                        stream,
                        writeGate,
                        "channel.bind",
                        request.Correlation,
                        new ChannelBindResponse { Tenant = body.Tenant, ServingInstance = 1 }).ConfigureAwait(false);
                    break;
                }

                case "DocumentReadRequest":
                {
                    var body = MessagePackSerializer.Deserialize<DocumentReadRequest>(request.Body, TbepOptions.Strict);
                    if (this.Documents.TryGetValue($"{tenant}/{body.Collection}/{body.Document}", out var document))
                    {
                        await this.AnswerAsync(
                            stream,
                            writeGate,
                            "document.read",
                            request.Correlation,
                            new DocumentReadResponse { Found = true, Document = document }).ConfigureAwait(false);
                    }
                    else
                    {
                        await this.AnswerAsync(
                            stream,
                            writeGate,
                            "document.read",
                            request.Correlation,
                            new DocumentReadResponse { Found = false }).ConfigureAwait(false);
                    }

                    break;
                }

                case "DocumentWriteRequest":
                {
                    var body = MessagePackSerializer.Deserialize<DocumentWriteRequest>(request.Body, TbepOptions.Strict);
                    var sequence = this.Commit(tenant!, body.Document.Collection, body.OperationId, body.Document.Id);
                    this.Documents[$"{tenant}/{body.Document.Collection}/{body.Document.Id}"] = new DocumentBody
                    {
                        Id = body.Document.Id,
                        Collection = body.Document.Collection,
                        Version = sequence,
                        Body = body.Document.Body,
                    };
                    await this.BroadcastAsync(tenant!, body.Document.Collection, subscriptions, stream, writeGate).ConfigureAwait(false);
                    await this.AnswerAsync(
                        stream,
                        writeGate,
                        "document.write",
                        request.Correlation,
                        new DocumentWriteResponse { Outcome = WriteOutcome.Committed(new CommitReceipt { Sequence = sequence, Version = sequence, OperationId = body.OperationId }) })
                        .ConfigureAwait(false);
                    break;
                }

                case "DocumentDeleteRequest":
                {
                    var body = MessagePackSerializer.Deserialize<DocumentDeleteRequest>(request.Body, TbepOptions.Strict);
                    var sequence = this.Commit(tenant!, body.Collection, body.OperationId, body.Document);
                    this.Documents.TryRemove($"{tenant}/{body.Collection}/{body.Document}", out _);
                    await this.BroadcastAsync(tenant!, body.Collection, subscriptions, stream, writeGate).ConfigureAwait(false);
                    await this.AnswerAsync(
                        stream,
                        writeGate,
                        "document.delete",
                        request.Correlation,
                        new DocumentDeleteResponse { Outcome = WriteOutcome.Committed(new CommitReceipt { Sequence = sequence, Version = sequence, OperationId = body.OperationId }) })
                        .ConfigureAwait(false);
                    break;
                }

                case "QueryExecuteRequest":
                case "QueryCancelRequest":
                case "FamilyReadRequest":
                case "FamilyWriteRequest":
                    await this.ErrorAsync(
                        stream,
                        writeGate,
                        request.Correlation,
                        ErrorCode.OperationRefused(),
                        "the scripted engine serves no query/family rows; the real engine e2e covers the typed refusal")
                        .ConfigureAwait(false);
                    break;

                case "BackboneSubscribeRequest":
                {
                    var body = MessagePackSerializer.Deserialize<BackboneSubscribeRequest>(request.Body, TbepOptions.Strict);
                    // The real engine honors the caller-assigned
                    // subscription identity (host.rs); so does the
                    // fixture — the host's hub owns the id space.
                    var id = body.SubscriptionId;
                    this.subscriptionTenants[id] = tenant!;
                    var from = body.From.Variant == "Sequence" ? (ulong)body.From.Payload! : 1ul;
                    var journal = this.journals.GetOrAdd(tenant!, []);
                    // The replay is collection-filtered and the
                    // subscription's cursor starts after it — both the
                    // real engine's semantics.
                    var events = journal
                        .Where(@event => @event.Sequence >= from)
                        .Where(@event => body.Collections is null || body.Collections.Contains(@event.Collection))
                        .ToArray();
                    var start = events.Length > 0 ? events[^1].Sequence + 1 : from;
                    subscriptions.Add(new Subscription(id, body.Collections, start));
                    await this.AnswerAsync(
                        stream,
                        writeGate,
                        "backbone.subscribe",
                        request.Correlation,
                        new BackboneSubscribeResponse { SubscriptionId = id, StartSequence = start }).ConfigureAwait(false);
                    if (events.Length > 0)
                    {
                        await this.NotifyAsync(
                            stream,
                            writeGate,
                            id,
                            new SubscriptionBatch { SubscriptionId = id, FromSequence = from, Events = events, NextSequence = start })
                            .ConfigureAwait(false);
                    }

                    break;
                }

                case "BackboneAcknowledgeRequest":
                {
                    var body = MessagePackSerializer.Deserialize<BackboneAcknowledgeRequest>(request.Body, TbepOptions.Strict);
                    this.subscriptionTenants.TryGetValue(body.SubscriptionId, out var owner);
                    if (owner is null || owner != tenant)
                    {
                        await this.RefuseLifecycleAsync(stream, writeGate, request.Correlation, "subscription", owner).ConfigureAwait(false);
                        continue;
                    }

                    await this.AnswerAsync(
                        stream,
                        writeGate,
                        "backbone.acknowledge",
                        request.Correlation,
                        new BackboneAcknowledgeResponse { SubscriptionId = body.SubscriptionId, AcknowledgedThrough = body.ThroughSequence }).ConfigureAwait(false);
                    break;
                }

                case "BackboneUnsubscribeRequest":
                {
                    var body = MessagePackSerializer.Deserialize<BackboneUnsubscribeRequest>(request.Body, TbepOptions.Strict);
                    this.subscriptionTenants.TryGetValue(body.SubscriptionId, out var owner);
                    if (owner is null || owner != tenant)
                    {
                        await this.RefuseLifecycleAsync(stream, writeGate, request.Correlation, "subscription", owner).ConfigureAwait(false);
                        continue;
                    }

                    this.subscriptionTenants.TryRemove(body.SubscriptionId, out _);
                    subscriptions.RemoveAll(subscription => subscription.Id == body.SubscriptionId);
                    await this.NotifyAsync(
                        stream,
                        writeGate,
                        body.SubscriptionId,
                        new SubscriptionClosed { SubscriptionId = body.SubscriptionId, Reason = SubscriptionCloseReason.Replaced() }).ConfigureAwait(false);
                    await this.AnswerAsync(
                        stream,
                        writeGate,
                        "backbone.unsubscribe",
                        request.Correlation,
                        new BackboneUnsubscribeResponse { SubscriptionId = body.SubscriptionId }).ConfigureAwait(false);
                    break;
                }

                case "SyncSessionSetupRequest":
                {
                    var body = MessagePackSerializer.Deserialize<SyncSessionSetupRequest>(request.Body, TbepOptions.Strict);
                    ulong id;
                    lock (this.idGate)
                    {
                        id = ++this.nextSession;
                    }

                    this.sessionTenants[id] = tenant!;
                    await this.AnswerAsync(
                        stream,
                        writeGate,
                        "sync.session_setup",
                        request.Correlation,
                        new SyncSessionSetupResponse { SessionId = id, Checkpoint = CheckpointOf(0), RetainedFloor = CheckpointOf(0) }).ConfigureAwait(false);
                    break;
                }

                case "SyncDeltaPullRequest":
                {
                    var body = MessagePackSerializer.Deserialize<SyncDeltaPullRequest>(request.Body, TbepOptions.Strict);
                    this.sessionTenants.TryGetValue(body.SessionId, out var owner);
                    if (owner is null || owner != tenant)
                    {
                        await this.RefuseLifecycleAsync(stream, writeGate, request.Correlation, "session", owner).ConfigureAwait(false);
                        continue;
                    }

                    var commits = (ulong)this.journals.GetOrAdd(tenant!, []).Count;
                    if (body.From.AsSpan().SequenceEqual(CheckpointOf(commits)))
                    {
                        await this.AnswerAsync(
                            stream,
                            writeGate,
                            "sync.delta_pull",
                            request.Correlation,
                            new SyncDeltaPullResponse
                            {
                                SessionId = body.SessionId,
                                PayloadCodec = "loro-delta/1",
                                Payload = [],
                                Next = CheckpointOf(commits),
                                Commits = 0,
                                SingleCommitOversize = false,
                            }).ConfigureAwait(false);
                    }
                    else
                    {
                        await this.ErrorAsync(stream, writeGate, request.Correlation, ErrorCode.CheckpointRefused(), "behind-retained-floor").ConfigureAwait(false);
                    }

                    break;
                }

                case "SyncSnapshotPullRequest":
                {
                    var body = MessagePackSerializer.Deserialize<SyncSnapshotPullRequest>(request.Body, TbepOptions.Strict);
                    this.sessionTenants.TryGetValue(body.SessionId, out var owner);
                    if (owner is null || owner != tenant)
                    {
                        await this.RefuseLifecycleAsync(stream, writeGate, request.Correlation, "session", owner).ConfigureAwait(false);
                        continue;
                    }

                    var commits = (ulong)this.journals.GetOrAdd(tenant!, []).Count;
                    await this.AnswerAsync(
                        stream,
                        writeGate,
                        "sync.snapshot_pull",
                        request.Correlation,
                        new SyncSnapshotPullResponse
                        {
                            SessionId = body.SessionId,
                            PayloadCodec = "loro-snapshot/1",
                            Payload = [1, 2, 3],
                            Checkpoint = CheckpointOf(commits),
                        }).ConfigureAwait(false);
                    break;
                }

                case "SyncCheckpointAckRequest":
                {
                    var body = MessagePackSerializer.Deserialize<SyncCheckpointAckRequest>(request.Body, TbepOptions.Strict);
                    this.sessionTenants.TryGetValue(body.SessionId, out var owner);
                    if (owner is null || owner != tenant)
                    {
                        await this.RefuseLifecycleAsync(stream, writeGate, request.Correlation, "session", owner).ConfigureAwait(false);
                        continue;
                    }

                    await this.AnswerAsync(
                        stream,
                        writeGate,
                        "sync.checkpoint_ack",
                        request.Correlation,
                        new SyncCheckpointAckResponse { SessionId = body.SessionId }).ConfigureAwait(false);
                    break;
                }

                case "SyncMutationSubmitRequest":
                {
                    var body = MessagePackSerializer.Deserialize<SyncMutationSubmitRequest>(request.Body, TbepOptions.Strict);
                    this.sessionTenants.TryGetValue(body.SessionId, out var owner);
                    if (owner is null || owner != tenant)
                    {
                        await this.RefuseLifecycleAsync(stream, writeGate, request.Correlation, "session", owner).ConfigureAwait(false);
                        continue;
                    }

                    var composite = body.Change.Variant == "Put"
                        && ((SyncField[])body.Change.Payload!).Any(f => f.Value.Tag is 5 or 6);
                    if (composite)
                    {
                        await this.AnswerAsync(
                            stream,
                            writeGate,
                            "sync.mutation_submit",
                            request.Correlation,
                            new SyncMutationSubmitResponse
                            {
                                SessionId = body.SessionId,
                                Outcome = MutationOutcome.Rejected(MutationReason.InvalidBody()),
                            }).ConfigureAwait(false);
                        continue;
                    }

                    var sequence = this.Commit(tenant!, "orders", request.Idempotency ?? "op", body.Document);
                    await this.BroadcastAsync(tenant!, "orders", subscriptions, stream, writeGate).ConfigureAwait(false);
                    await this.AnswerAsync(
                        stream,
                        writeGate,
                        "sync.mutation_submit",
                        request.Correlation,
                        new SyncMutationSubmitResponse
                        {
                            SessionId = body.SessionId,
                            Outcome = MutationOutcome.Applied(new CommitReceipt
                            {
                                Sequence = sequence,
                                Version = sequence,
                                OperationId = request.Idempotency ?? "op",
                            }),
                        }).ConfigureAwait(false);
                    break;
                }

                case "SyncSessionCloseRequest":
                {
                    var body = MessagePackSerializer.Deserialize<SyncSessionCloseRequest>(request.Body, TbepOptions.Strict);
                    this.sessionTenants.TryGetValue(body.SessionId, out var owner);
                    if (owner is null || owner != tenant)
                    {
                        await this.RefuseLifecycleAsync(stream, writeGate, request.Correlation, "session", owner).ConfigureAwait(false);
                        continue;
                    }

                    this.sessionTenants.TryRemove(body.SessionId, out _);
                    await this.AnswerAsync(
                        stream,
                        writeGate,
                        "sync.session_close",
                        request.Correlation,
                        new SyncSessionCloseResponse { SessionId = body.SessionId }).ConfigureAwait(false);
                    break;
                }

                case "TopologyReadRequest":
                    await this.AnswerAsync(
                        stream,
                        writeGate,
                        "topology.read",
                        request.Correlation,
                        new TopologyReadResponse { Binding = BindingKind.LocalUds(), Subsystems = ["scripted"], EngineTopologyAvailable = false }).ConfigureAwait(false);
                    break;

                case "AdminStatusRequest":
                    await this.AnswerAsync(
                        stream,
                        writeGate,
                        "admin.status",
                        request.Correlation,
                        new AdminStatusResponse
                        {
                            RegistryVersion = TbepRegistry.RegistryVersion,
                            FramesServed = (ulong)this.Received.Count,
                            Sessions = (uint)this.sessionTenants.Count,
                            Subscriptions = (uint)this.subscriptionTenants.Count,
                            Scopes = 0,
                        }).ConfigureAwait(false);
                    break;

                case "SupervisionHealthRequest":
                    await this.AnswerAsync(
                        stream,
                        writeGate,
                        "supervision.health",
                        request.Correlation,
                        new SupervisionHealthResponse
                        {
                            State = this.Draining ? HealthState.Draining() : HealthState.Serving(),
                            Sessions = (uint)this.sessionTenants.Count,
                            Subscriptions = (uint)this.subscriptionTenants.Count,
                            FramesServed = (ulong)this.Received.Count,
                        }).ConfigureAwait(false);
                    break;

                case "SupervisionShutdownRequest":
                    this.Draining = true;
                    foreach (var subscription in subscriptions)
                    {
                        await this.NotifyAsync(
                            stream,
                            writeGate,
                            subscription.Id,
                            new SubscriptionClosed { SubscriptionId = subscription.Id, Reason = SubscriptionCloseReason.ShuttingDown() }).ConfigureAwait(false);
                    }

                    await this.AnswerAsync(
                        stream,
                        writeGate,
                        "supervision.shutdown",
                        request.Correlation,
                        new SupervisionShutdownResponse { State = HealthState.Draining(), InFlight = (uint)subscriptions.Count }).ConfigureAwait(false);
                    return;

                default:
                    await this.ErrorAsync(stream, writeGate, request.Correlation, ErrorCode.MalformedFrame(), "wrong direction").ConfigureAwait(false);
                    break;
            }
        }
    }

    private ulong Commit(string tenant, string collection, string operationId, string document)
    {
        var journal = this.journals.GetOrAdd(tenant, []);
        lock (journal)
        {
            var sequence = this.nextSequences.AddOrUpdate(tenant, 1, (_, current) => current + 1);
            journal.Add(new ChangeEvent
            {
                Sequence = sequence,
                OperationId = operationId,
                Kind = ChangeKind.Put(),
                Collection = collection,
                Document = document,
            });
            return sequence;
        }
    }

    private async Task BroadcastAsync(
        string tenant,
        string collection,
        List<Subscription> subscriptions,
        NetworkStream stream,
        SemaphoreSlim writeGate)
    {
        var journal = this.journals.GetOrAdd(tenant, []);
        ChangeEvent[] events;
        lock (journal)
        {
            events = [.. journal];
        }

        foreach (var subscription in subscriptions)
        {
            if (subscription.Collections is not null && !subscription.Collections.Contains(collection))
            {
                continue;
            }

            // Only the events after the subscription's cursor — the
            // real engine never re-batches what a consumer already
            // received.
            var matching = events
                .Where(@event => @event.Sequence >= subscription.Next && @event.Collection == collection)
                .ToArray();
            if (matching.Length == 0)
            {
                continue;
            }

            subscription.Next = matching[^1].Sequence + 1;
            await this.NotifyAsync(
                stream,
                writeGate,
                subscription.Id,
                new SubscriptionBatch
                {
                    SubscriptionId = subscription.Id,
                    FromSequence = matching[0].Sequence,
                    Events = matching,
                    NextSequence = subscription.Next,
                }).ConfigureAwait(false);
            await this.NotifyAsync(
                stream,
                writeGate,
                subscription.Id,
                new TriggerNotification
                {
                    SubscriptionId = subscription.Id,
                    Collection = collection,
                    Sequence = matching[^1].Sequence,
                    OperationId = matching[^1].OperationId,
                }).ConfigureAwait(false);
        }
    }

    private static byte[] CheckpointOf(ulong commits) => [(byte)(commits & 0xff), 0x6f, 0x70];

    private async Task RefuseLifecycleAsync(
        NetworkStream stream,
        SemaphoreSlim writeGate,
        ulong causation,
        string kind,
        string? owner)
    {
        // The real engine's two typed refusals (host.rs): an unknown id
        // and another tenant's id are distinct details.
        var detail = owner is null ? $"unknown {kind}" : "bound to another channel";
        await this.ErrorAsync(
            stream,
            writeGate,
            causation,
            kind == "session" ? ErrorCode.SessionRefused() : ErrorCode.SubscriptionRefused(),
            detail).ConfigureAwait(false);
    }

    private async Task AnswerAsync<T>(NetworkStream stream, SemaphoreSlim writeGate, string operation, ulong causation, T body)
    {
        var bytes = MessagePackSerializer.Serialize(body, TbepOptions.Strict);
        await WriteSealedAsync(stream, writeGate, TbepRegistry.ResponseKindOf(operation), correlation: 1, causation, bytes).ConfigureAwait(false);
    }

    private async Task ErrorAsync(NetworkStream stream, SemaphoreSlim writeGate, ulong causation, ErrorCode code, string detail)
    {
        var bytes = MessagePackSerializer.Serialize(new Error { Code = code, Detail = detail }, TbepOptions.Strict);
        await WriteSealedAsync(stream, writeGate, TbepRegistry.KindCode("Error"), correlation: 1, causation, bytes).ConfigureAwait(false);
    }

    private async Task NotifyAsync<T>(NetworkStream stream, SemaphoreSlim writeGate, ulong subscriptionId, T body)
    {
        var bytes = MessagePackSerializer.Serialize(body, TbepOptions.Strict);
        var kind = body switch
        {
            SubscriptionBatch => "SubscriptionBatch",
            TriggerNotification => "TriggerNotification",
            SubscriptionClosed => "SubscriptionClosed",
            _ => throw new InvalidOperationException($"no notification kind for {body?.GetType().Name}"),
        };
        await WriteSealedAsync(stream, writeGate, TbepRegistry.KindCode(kind), correlation: 0, subscriptionId, bytes).ConfigureAwait(false);
    }

    private static async Task WriteSealedAsync(
        NetworkStream stream,
        SemaphoreSlim writeGate,
        ushort kind,
        ulong correlation,
        ulong causation,
        byte[] body)
    {
        var frame = TbepWire.Seal(kind, correlation, causation, idempotency: null, body);
        await writeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await TbepWire.WriteFrame(stream, frame).ConfigureAwait(false);
        }
        finally
        {
            writeGate.Release();
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        this.stopped.Cancel();
        this.listener.Dispose();
        await this.serving.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        if (File.Exists(this.Path))
        {
            File.Delete(this.Path);
        }

        this.stopped.Dispose();
    }

    /// <summary>One live backbone subscription of a connection: its identity, filter, and cursor.</summary>
    private sealed class Subscription(ulong id, string[]? collections, ulong next)
    {
        /// <summary>The caller-assigned subscription identity.</summary>
        public ulong Id { get; } = id;

        /// <summary>The collection filter, or <c>null</c> for every collection.</summary>
        public string[]? Collections { get; } = collections;

        /// <summary>The next sequence this subscription has not served.</summary>
        public ulong Next { get; set; } = next;
    }
}

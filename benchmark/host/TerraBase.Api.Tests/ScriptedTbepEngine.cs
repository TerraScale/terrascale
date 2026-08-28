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
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, CommitReceipt>> appliedKeys = new();
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

    /// <summary>
    /// The expected-version conditional of every write and delete the
    /// engine received, rendered with its bound tenant (the conditional
    /// mapping rider's proof surface).
    /// </summary>
    public ConcurrentQueue<string> WriteConditionals { get; } = [];

    /// <summary>The scripted document read answers, keyed by collection/document.</summary>
    public ConcurrentDictionary<string, DocumentBody> Documents { get; } = new();

    /// <summary>When set, the engine refuses every new binding (a draining engine).</summary>
    public bool Draining { get; set; }

    /// <summary>
    /// When set, the scripted engine owns the named collection for another
    /// tenant, so a schema declaration for it is refused typed (the
    /// engine-enforced tenant isolation of the schema surface).
    /// </summary>
    public (string Collection, string Tenant)? SchemaOwner { get; set; }

    private readonly Dictionary<string, ulong> schemas = new();

    private readonly HashSet<string> appliedMigrations = new();

    private ulong NextSchemaVersion(string collection) => this.schemas.TryGetValue(collection, out var current) ? current + 1 : 1;

    private async Task RefuseCrossTenantAsync(NetworkStream stream, SemaphoreSlim writeGate, ulong causation)
    {
        await this.ErrorAsync(
            stream,
            writeGate,
            causation,
            ErrorCode.OperationRefused(),
            "bound to another channel")
            .ConfigureAwait(false);
    }

    /// <summary>
    /// The delay before the engine answers a channel binding: a window
    /// wide enough for two host callers to race the same first bind
    /// deterministically (default: none).
    /// </summary>
    public TimeSpan BindDelay { get; set; }

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

                    if (this.BindDelay > TimeSpan.Zero)
                    {
                        // The deterministic first-bind race window: the
                        // answer stays outstanding long enough for a
                        // second caller to reach the same channel.
                        await Task.Delay(this.BindDelay).ConfigureAwait(false);
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

                case "SchemaDeclareRequest":
                {
                    // The SM-04 scripted schema surface: a deterministic
                    // declaration that records the collection's version for
                    // the bound tenant, or refuses typed when the scripted
                    // engine owns the collection for another tenant.
                    var body = MessagePackSerializer.Deserialize<SchemaDeclareRequest>(request.Body, TbepOptions.Strict);
                    if (this.SchemaOwner is { } owner && owner.Collection == body.Collection && owner.Tenant != tenant)
                    {
                        await this.RefuseCrossTenantAsync(stream, writeGate, request.Correlation).ConfigureAwait(false);
                        break;
                    }

                    this.Received.Enqueue($"{tenant}:schema.declare:{body.Collection}");
                    var ruleCount = (uint)(body.Schema is null ? 0 : 3);
                    var version = body.Schema is null ? (ulong?)null : this.NextSchemaVersion(body.Collection);
                    if (version is { } declared)
                    {
                        this.schemas[$"{tenant}/{body.Collection}"] = declared;
                    }

                    await this.AnswerAsync(
                        stream,
                        writeGate,
                        "schema.declare",
                        request.Correlation,
                        new SchemaDeclareResponse { EnforcedVersion = this.schemas.TryGetValue($"{tenant}/{body.Collection}", out var recorded) ? recorded : null, RuleCount = ruleCount })
                        .ConfigureAwait(false);
                    break;
                }

                case "SchemaStatusRequest":
                {
                    var body = MessagePackSerializer.Deserialize<SchemaStatusRequest>(request.Body, TbepOptions.Strict);
                    this.Received.Enqueue($"{tenant}:schema.status:{body.Collection}");
                    await this.AnswerAsync(
                        stream,
                        writeGate,
                        "schema.status",
                        request.Correlation,
                        new SchemaStatusResponse
                        {
                            EnforcedVersion = this.schemas.TryGetValue($"{tenant}/{body.Collection}", out var version) ? version : null,
                            RuleCount = this.schemas.ContainsKey($"{tenant}/{body.Collection}") ? 3u : 0u,
                        })
                        .ConfigureAwait(false);
                    break;
                }

                case "SchemaValidateRequest":
                {
                    var body = MessagePackSerializer.Deserialize<SchemaValidateRequest>(request.Body, TbepOptions.Strict);
                    this.Received.Enqueue($"{tenant}:schema.validate:{body.Collection}");
                    var violations = new List<SchemaViolation>();
                    if (this.schemas.TryGetValue($"{tenant}/{body.Collection}", out var version)
                        && body.Document is { Tag: 6 } document
                        && document.Payload is ObjectField[] fields
                        && fields.Any(field => field.Name == "email" && field.Value.Tag == 2))
                    {
                        violations.Add(new SchemaViolation { Field = "email", Kind = ViolationKind.TypeMismatch() });
                    }

                    await this.AnswerAsync(
                        stream,
                        writeGate,
                        "schema.validate",
                        request.Correlation,
                        new SchemaValidateResponse
                        {
                            EnforcedVersion = this.schemas.TryGetValue($"{tenant}/{body.Collection}", out var enforced) ? enforced : null,
                            Valid = violations.Count == 0,
                            Violations = [.. violations],
                        })
                        .ConfigureAwait(false);
                    break;
                }

                case "MigrationStatusRequest":
                {
                    var body = MessagePackSerializer.Deserialize<MigrationStatusRequest>(request.Body, TbepOptions.Strict);
                    this.Received.Enqueue($"{tenant}:migration.status:{body.Collection}");
                    var applied = this.appliedMigrations.Contains($"{tenant}/{body.Collection}");
                    await this.AnswerAsync(
                        stream,
                        writeGate,
                        "migration.status",
                        request.Correlation,
                        new MigrationStatusResponse
                        {
                            State = new MigrationState
                            {
                                Kind = applied ? MigrationStateKind.Complete() : MigrationStateKind.None(),
                                Manifest = "0123abcd",
                                FromVersion = 1,
                                ToVersion = 2,
                                Documents = 0,
                                Transformed = 0,
                                Pages = 0,
                            },
                        })
                        .ConfigureAwait(false);
                    break;
                }

                case "MigrationDryRunRequest":
                {
                    var body = MessagePackSerializer.Deserialize<MigrationDryRunRequest>(request.Body, TbepOptions.Strict);
                    this.Received.Enqueue($"{tenant}:migration.dry_run:{body.Contract.Collection}");
                    await this.AnswerAsync(
                        stream,
                        writeGate,
                        "migration.dry_run",
                        request.Correlation,
                        new MigrationDryRunResponse
                        {
                            Plan = new MigrationPlan
                            {
                                Manifest = "0123abcd",
                                Collection = body.Contract.Collection,
                                FromVersion = body.Contract.FromVersion,
                                ToVersion = body.Contract.ToVersion,
                                Ready = true,
                                Documents = 1,
                                SourceDigest = new string('a', 64),
                                ResultDigest = new string('b', 64),
                                TransformCounts = new TransformCounts { Added = 1, Removed = 1, Renamed = 0, Retyped = 0 },
                                Problems = [],
                                ProblemsTruncated = false,
                            },
                        })
                        .ConfigureAwait(false);
                    break;
                }

                case "MigrationApplyRequest":
                {
                    var body = MessagePackSerializer.Deserialize<MigrationApplyRequest>(request.Body, TbepOptions.Strict);
                    this.Received.Enqueue($"{tenant}:migration.apply:{body.Contract.Collection}");
                    this.schemas[$"{tenant}/{body.Contract.Collection}"] = body.Contract.ToVersion;
                    this.appliedMigrations.Add($"{tenant}/{body.Contract.Collection}");
                    await this.AnswerAsync(
                        stream,
                        writeGate,
                        "migration.apply",
                        request.Correlation,
                        new MigrationApplyResponse
                        {
                            Receipt = new MigrationReceipt
                            {
                                Collection = body.Contract.Collection,
                                Manifest = "0123abcd",
                                FromVersion = body.Contract.FromVersion,
                                ToVersion = body.Contract.ToVersion,
                                SourceDigest = new string('a', 64),
                                ResultDigest = new string('b', 64),
                                Documents = 1,
                                Transformed = 1,
                                TransformCounts = new TransformCounts { Added = 1, Removed = 1, Renamed = 0, Retyped = 0 },
                                Pages = 1,
                                RollbackLimit = RollbackLimitKind.NotReversible(),
                                AcknowledgedNotRollbackable = body.AcknowledgeNotRollbackable,
                                AlreadyApplied = false,
                            },
                        })
                        .ConfigureAwait(false);
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
                    this.WriteConditionals.Enqueue($"{tenant}:write:{Render(body.Expected)}");
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
                    this.WriteConditionals.Enqueue($"{tenant}:delete:{Render(body.Expected)}");
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
                    if (body.Resume is { } resume && !Decodes(resume))
                    {
                        // The real engine refuses a resume checkpoint that
                        // does not decode, fail-closed.
                        await this.ErrorAsync(stream, writeGate, request.Correlation, ErrorCode.CheckpointRefused(), "the resume checkpoint does not decode").ConfigureAwait(false);
                        continue;
                    }

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

                    if (!Decodes(body.Checkpoint))
                    {
                        await this.ErrorAsync(stream, writeGate, request.Correlation, ErrorCode.CheckpointRefused(), "the checkpoint does not decode").ConfigureAwait(false);
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

                    // At-least-once: a retried key answers the recorded
                    // receipt (the real engine's bounded applied-key
                    // registry, 128 receipts per scope, drop-oldest).
                    var key = request.Idempotency ?? "op";
                    var receipts = this.appliedKeys.GetOrAdd(tenant!, _ => new ConcurrentDictionary<string, CommitReceipt>());
                    if (receipts.TryGetValue(key, out var recorded))
                    {
                        await this.AnswerAsync(
                            stream,
                            writeGate,
                            "sync.mutation_submit",
                            request.Correlation,
                            new SyncMutationSubmitResponse
                            {
                                SessionId = body.SessionId,
                                Outcome = MutationOutcome.Applied(recorded),
                            }).ConfigureAwait(false);
                        continue;
                    }

                    var sequence = this.Commit(tenant!, "orders", key, body.Document);
                    var receipt = new CommitReceipt { Sequence = sequence, Version = sequence, OperationId = key };
                    lock (receipts)
                    {
                        receipts[key] = receipt;
                        while (receipts.Count > 128)
                        {
                            receipts.TryRemove(receipts.Keys.First(), out _);
                        }
                    }

                    await this.BroadcastAsync(tenant!, "orders", subscriptions, stream, writeGate).ConfigureAwait(false);
                    await this.AnswerAsync(
                        stream,
                        writeGate,
                        "sync.mutation_submit",
                        request.Correlation,
                        new SyncMutationSubmitResponse
                        {
                            SessionId = body.SessionId,
                            Outcome = MutationOutcome.Applied(receipt),
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

    /// <summary>
    /// Ends a session on the engine's own initiative (an idle expiry,
    /// say) — the host hears nothing: no refusal, no generation loss.
    /// </summary>
    /// <param name="sessionId">The session the engine ends.</param>
    public void ForgetSession(ulong sessionId) => this.sessionTenants.TryRemove(sessionId, out _);

    /// <summary>Renders the expected-version conditional the engine received.</summary>
    /// <param name="expected">The conditional of the write or delete.</param>
    /// <returns>The rendered conditional.</returns>
    private static string Render(ExpectedVersion expected) => expected.Variant switch
    {
        "Any" => "any",
        "Exact" => $"exact:{(ulong)expected.Payload!}",
        _ => "absent",
    };

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

    private static bool Decodes(byte[] checkpoint) =>
        checkpoint.Length == 3 && checkpoint[1] == 0x6f && checkpoint[2] == 0x70;

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

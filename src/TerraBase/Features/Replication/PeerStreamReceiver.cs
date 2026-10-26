using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Google.Protobuf;
using Grpc.Core;
using TerraBase.Configuration;
using TerraBase.Engine;
using TerraBase.Engine.Lattices;
using TerraBase.Features.Management.Usage;
using TerraBase.Features.Memory;
using TerraBase.Features.Qos;
using TerraBase.Features.Replication.V1;
using TerraBase.Hosting;
using TerraBase.Tenancy;
using ZLogger;

namespace TerraBase.Features.Replication;

/// <summary>
/// The receiving side of <c>Replication.Push</c>: joins each batch through the <see cref="IChangeGroupSink"/>, in stream
/// order per database, and acks it after its durable commit. Databases apply beside each other, so one waiting on its busy
/// writer holds back no other database's ack; the link loop alone writes answers. A newer stream from the same origin ends the older one and starts once
/// it ended, so one origin's groups apply in order. A refill runs beside the stream: other databases' batches go on, while
/// its own database's are refused until a refill of it commits, also on the origin's next stream when this one ends first.
/// A batch that fails to apply is refused for its database only. An undecodable batch is refused for its database; the sender reconnects
/// and resumes from its last ack. A protocol 2 sender keeps up to <see cref="ReplicationProtocol.Window"/> batches of a database
/// in flight, chained by <c>Batch.after</c>: each applies only after the one it follows applied, so an ack still covers every
/// batch before it, and after a refusal the database's batches are refused until one follows the last applied batch again.
/// The batch whose turn comes applies the ones queued behind it in the same durable transaction, up to one batch's groups and
/// bytes and one transaction's deltas, and each still gets its own ack, in order, once that transaction and its stable read
/// succeeded; a refusal of the transaction refuses them all.
/// </summary>
internal sealed partial class PeerStreamReceiver : V1.Replication.ReplicationBase
{
    private const int MaxRefills = 4;
    // Databases with a batch or frontier in progress per stream: room for the sender's 32 outstanding batches, plus as many
    // optional frontiers, which are dropped beyond it, so a compliant sender's batch is never refused for this bound.
    private const int MaxApplying = 64;
    private const long BucketReadBytes = 16L * ReplicationProtocol.MaxApplicationBytes;
    // Two digests and one page's wire, decompressed, parsed and decoded payloads; held until the sink finishes.
    private const long RefillBytes = (2 * 4096 * 16) + (4L * ReplicationProtocol.MaxApplicationBytes);
    private readonly IChangeGroupSink _sink;
    private readonly IReplicaStates _states;
    private readonly IReplicaCopyFaults _faults;
    private readonly AntiEntropy _antiEntropy;
    private readonly LocalReplica _self;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<ReplicaId, (CancellationTokenSource Stop, Task Ended)> _streams = new();
    // Per origin, databases whose refill did not commit. A new sender incarnation does not repair the missing state.
    private readonly ConcurrentDictionary<ReplicaId, HashSet<TenantContext>> _cut = new();
    private readonly Counter<long> _merged;
    private readonly Histogram<double> _delay;
    private readonly Counter<long> _undecodable;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<PeerStreamReceiver> _logger;
    private readonly IMemoryBudget _memory;
    private readonly ReplicationTimings _timings;
    private readonly IUsageMeter _usage;
    private readonly LogChunks _chunks;
    private int _refilling;

    // A test observes a replacement while its predecessor's cancelled repair is still ending.
    internal Func<ReplicaId, CancellationToken, Task>? Replacing { get; set; }

    private readonly Lazy<ICatalogGate> _catalogGate;

    public PeerStreamReceiver(
        IChangeGroupSink sink, IReplicaStates states, AntiEntropy antiEntropy, LocalReplica self, TimeProvider time, IMeterFactory meters, IHostApplicationLifetime lifetime,
        ILogger<PeerStreamReceiver> logger, IMemoryBudget memory, Lazy<ICatalogGate> catalogGate, IReplicaCopyFaults faults, ReplicationTimings timings, IUsageMeter usage, LogChunks chunks)
    {
        _timings = timings;
        _usage = usage;
        _chunks = chunks;
        _catalogGate = catalogGate;
        _memory = memory;
        _logger = logger;
        _sink = sink;
        _states = states;
        _faults = faults;
        _antiEntropy = antiEntropy;
        _lifetime = lifetime;
        _logger = logger;
        _self = self;
        _time = time;
        var meter = meters.Create(Telemetry.MeterName);
        _merged = meter.CreateCounter<long>("terrabase.replication.merged_groups", "{group}", "Change groups joined from peers, by the origin's region.");
        _delay = meter.CreateHistogram<double>("terrabase.replication.apply_delay", "s",
            "Apply time minus the origin stamp's physical time: convergence lag, by the origin's region.",
            // The step buckets, so a p99 between 100 and 500 ms is not read off one bucket's edge; then catch-up's range.
            advice: new InstrumentAdvice<double> { HistogramBucketBoundaries = [.. ReplicationTimings.Buckets, 30, 60, 300] });
        _undecodable = meter.CreateCounter<long>("terrabase.replication.undecodable_batches", "{batch}",
            "Peer batches refused because they could not be decoded, by the origin's region.");
    }

    public override async Task Push(IAsyncStreamReader<PushFrame> requestStream, IServerStreamWriter<AckFrame> responseStream, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(requestStream);
        ArgumentNullException.ThrowIfNull(responseStream);
        ArgumentNullException.ThrowIfNull(context);
        // Shutdown ends the stream, also one still waiting for its hello; the sender resumes from its last ack elsewhere or later.
        // So does a sender silent for the progress timeout (it heartbeats every second): a half-open connection, or one that
        // stopped reading, where this side's writes stall. Aborting the request also ends a write that waits for the sender.
        using var silent = new CancellationTokenSource(ReplicationProtocol.ProgressTimeout, _time);
        using var abort = silent.Token.Register(static http => ((HttpContext)http!).Abort(), context.GetHttpContext());
        using var stream = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken, _lifetime.ApplicationStopping);
        Hello hello;
        try
        {
            // Start the accepted response before waiting for the hello, so shutdown can end it cleanly while it waits.
            await context.WriteResponseHeadersAsync(Metadata.Empty).ConfigureAwait(false);
            if (!await requestStream.MoveNext(stream.Token).ConfigureAwait(false) || requestStream.Current.Hello is not { } first)
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument, "a push stream starts with a hello"));
            }

            hello = first;
            silent.CancelAfter(ReplicationProtocol.ProgressTimeout);
        }
        catch (Exception) when (silent.IsCancellationRequested || (stream.IsCancellationRequested && !context.CancellationToken.IsCancellationRequested))
        {
            return;
        }

        var origin = new ReplicaId(hello.Node);
        if (origin == _self.Id || hello.Node == 0)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "a push stream comes from another machine"));
        }

        _self.Presence.Heard(hello.Machine, origin, _self.Clock.Tick().Milliseconds);
        var region = TerraBaseOptions.IsRegion(hello.Region) ? hello.Region : "unknown";
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationTokenSource? precedingStop = null;
        var precedingEnded = Task.CompletedTask;
        while (true)
        {
            if (_streams.TryGetValue(origin, out var older))
            {
                if (!_streams.TryUpdate(origin, (stream, ended.Task), older))
                {
                    continue;
                }

                precedingStop = older.Stop;
                precedingEnded = older.Ended;
                break;
            }

            if (_streams.TryAdd(origin, (stream, ended.Task)))
            {
                break;
            }
        }
        // Keyed by database and the incarnation of the origin's copy its frames name: change ids of one incarnation say
        // nothing of another's, which restart.
        var last = new Dictionary<TenantContext, (Ulid Incarnation, ulong ChangeId)>();
        // A protocol 2 sender's chains, per database (Chain).
        var pipelined = ReplicationProtocol.Negotiate(hello.Protocol) >= 2;
        var chains = new Dictionary<TenantContext, Chain>();
        // The databases a refill runs for, and those whose last refill failed: their batches are refused until a refill of
        // them commits, so none joins before the repairs it follows.
        var refills = new Dictionary<(TenantContext Database, Ulid Incarnation), (Refill Frame, Task<int?> Done)>();
        HashSet<TenantContext> cut = [];
        // Per database, its newest batch or frontier in progress; the next one of the database runs after it.
        var applying = new Dictionary<TenantContext, (Task<AckFrame?> Done, ApplyProgress? Progress, Queued? Batch)>();
        // Every batch in progress with its work, reported on heartbeats: a pipelined database has several, applied in turn.
        var working = new List<(Task<AckFrame?> Done, ApplyProgress Progress, V1.Database Wire, ulong ChangeId)>();
        // Batches and frontiers in arrival order. A database's frames run one after another, so they also complete in this order.
        var answers = new List<(TenantContext Database, Ulid Incarnation, Task<AckFrame?> Done, long Received)>();
        // Client databases a batch of this stream applied: their registration is already committed here.
        HashSet<TenantContext> registered = [];
        // A client database's batch or refill also follows the catalog's frame in progress: the sender lists the system
        // database first, and a database it registers is refused until that registration commits. Once one of its batches
        // applied, later ones need no catalog frame (TS-974: they queued behind every catalog apply of a busy origin).
        Task Catalog(TenantContext database)
        {
            return database != ChangeGroups.System && !registered.Contains(database) && applying.TryGetValue(ChangeGroups.System, out var system)
                ? system.Done : Task.CompletedTask;
        }

        Task After(TenantContext database)
        {
            Task own = applying.TryGetValue(database, out var pending) ? pending.Done : Task.CompletedTask;
            var catalog = Catalog(database);
            return own.IsCompletedSuccessfully ? catalog
                : catalog.IsCompletedSuccessfully ? own
                : Task.WhenAll(own, catalog);
        }
        // Whether a protocol 2 batch continues its database's chain on this stream, and if so takes its place in it.
        bool Admit(TenantContext database, Ulid incarnation, Batch batch, bool busy)
        {
            if (!chains.TryGetValue(database, out var chain) || chain.Incarnation != incarnation)
            {
                // Another copy of the sender's starts a chain at its resume position, once the last copy's batches ended;
                // refused before then, it is broken: only a resend from that position restarts it.
                var ended = chain is not { Pending: > 0 };
                chain = chains[database] = new Chain(incarnation, batch.After) { Broken = !ended };
                if (!ended)
                {
                    return false;
                }
            }

            if (chain.Broken)
            {
                // Restarted only by a batch following the last applied one, once the batches it may follow all ended.
                if (chain.Pending > 0 || batch.After != chain.Applied)
                {
                    return false;
                }

                (chain.Broken, chain.Expected) = (false, chain.Applied);
            }

            if (batch.After != chain.Expected || chain.Pending >= ReplicationProtocol.Window || (!busy && applying.Count >= MaxApplying))
            {
                return false;
            }

            (chain.Pending, chain.Expected) = (chain.Pending + 1, batch.LastChangeId);
            return true;
        }

        Task<bool>? reading = null;
        // When the frame arrived, not when this loop got to it (receive.loop): batch steps count from arrival.
        long arrived = 0;
        async Task<bool> NextAsync()
        {
            var more = await requestStream.MoveNext(stream.Token).ConfigureAwait(false);
            arrived = _timings.Now;
            return more;
        }

        try
        {
            if (precedingStop is not null)
            {
                if (Replacing is { } replacing)
                {
                    await replacing(origin, stream.Token).ConfigureAwait(false);
                }
                await CancelPreviousAsync(precedingStop, precedingEnded).ConfigureAwait(false);
                await precedingEnded.WaitAsync(stream.Token).ConfigureAwait(false);
            }

            cut = _cut.TryRemove(origin, out var carried) ? carried : [];
            // An already-ended predecessor can satisfy WaitAsync after this stream was cancelled.
            stream.Token.ThrowIfCancellationRequested();
            await responseStream.WriteAsync(new AckFrame { Welcome = new Welcome { Node = _self.Id.Value, Protocol = ReplicationProtocol.Version, Chunks = _chunks.Reads } }, stream.Token).ConfigureAwait(false);
            reading = NextAsync();
            while (true)
            {
                // A refill may take minutes: it runs beside the frames, so other databases' batches are still acked in time.
                // Only each database's oldest answer can go next: a later one may complete first, but is written after it.
                _ = await Task.WhenAny([reading, .. refills.Values.Select(refilling => refilling.Done),
                        .. answers.DistinctBy(answer => answer.Database).Select(answer => answer.Done)])
                    .ConfigureAwait(false);
                // Before refills: a refill that waited for its database's batch is answered after it.
                HashSet<TenantContext>? waiting = null;
                for (var index = 0; index < answers.Count;)
                {
                    var answer = answers[index];
                    if (!answer.Done.IsCompleted || waiting?.Contains(answer.Database) == true)
                    {
                        (waiting ??= []).Add(answer.Database);
                        index++;
                        continue;
                    }

                    answers.RemoveAt(index);
                    if (applying.TryGetValue(answer.Database, out var newest) && newest.Done == answer.Done)
                    {
                        _ = applying.Remove(answer.Database);
                    }

                    var response = await answer.Done.ConfigureAwait(false);
                    if (answer.Database != ChangeGroups.System && response?.Ack is { Refused: false })
                    {
                        _ = registered.Add(answer.Database);
                    }
                    if (answer.Incarnation != default && chains.TryGetValue(answer.Database, out var chain) && chain.Incarnation == answer.Incarnation)
                    {
                        chain.Pending--;
                        chain.Broken |= response?.Ack is not { Refused: false };
                        chain.Applied = response?.Ack is { Refused: false } done ? done.ChangeId : chain.Applied;
                    }

                    if (response is null)
                    {
                        continue;
                    }

                    if (response.Ack is { RefillRequired: true } || response.Stable is { RefillRequired: true })
                    {
                        _ = cut.Add(answer.Database);
                    }

                    if (response.Ack is { Refused: false } applied)
                    {
                        last[answer.Database] = (answer.Incarnation, applied.ChangeId);
                    }

                    await responseStream.WriteAsync(response, stream.Token).ConfigureAwait(false);
                    if (response.Ack is { Refused: false })
                    {
                        _ = _timings.Record(ReplicationTimings.ReceiveTotal, answer.Received);
                    }
                }

                foreach (var (tenant, (asked, done)) in refills.Where(entry => entry.Value.Done.IsCompleted).ToList())
                {
                    var refilled = await done.ConfigureAwait(false) is not null;
                    if (await _faults.RefusedAsync(ChangeGroups.System, Ulid.Empty, stream.Token).ConfigureAwait(false))
                    {
                        throw new ReplicaCopyFaultedException("storage failed closed; this node cannot acknowledge a refill");
                    }
                    _ = refills.Remove(tenant);
                    if (refilled)
                    {
                        _ = cut.Remove(tenant.Database);
                        last[tenant.Database] = (tenant.Incarnation, asked.ChangeId);
                        chains[tenant.Database] = new Chain(tenant.Incarnation, asked.ChangeId);
                    }
                    else
                    {
                        _ = cut.Add(tenant.Database);
                    }

                    await responseStream.WriteAsync(new AckFrame
                    {
                        Ack = new Ack { Database = asked.Database, ChangeId = refilled ? asked.ChangeId : 0, Refused = !refilled },
                    }, stream.Token).ConfigureAwait(false);
                }

                if (!reading.IsCompleted)
                {
                    continue;
                }

                if (!await reading.ConfigureAwait(false))
                {
                    break;
                }

                var frame = requestStream.Current;
                var received = arrived;
                _ = _timings.Record(ReplicationTimings.ReceiveLoop, received);
                silent.CancelAfter(ReplicationProtocol.ProgressTimeout);
                reading = NextAsync();
                if (frame.KindCase == PushFrame.KindOneofCase.None)
                {
                    // A kind of a newer protocol than this build's: its sender knows this machine does not read it.
                    continue;
                }

                if (frame.KindCase == PushFrame.KindOneofCase.Heartbeat)
                {
                    _ = working.RemoveAll(active => active.Done.IsCompleted);
                    foreach (var active in working)
                    {
                        if (active.Progress.Value is var value && value > active.Progress.Reported)
                        {
                            await responseStream.WriteAsync(new AckFrame
                            {
                                Applying = new Applying { Database = active.Wire, ChangeId = active.ChangeId, Progress = (ulong)value },
                            }, stream.Token).ConfigureAwait(false);
                            active.Progress.Reported = value;
                        }
                    }
                    // An empty frame answers it, so the sender's deadline for answers also holds on an idle link.
                    await responseStream.WriteAsync(new AckFrame(), stream.Token).ConfigureAwait(false);
                    continue;
                }

                if (frame.Frontier is { } frontier)
                {
                    var at = Tenant(frontier.Database);
                    _ = Incarnation(frontier.Database);
                    if (cut.Contains(at) || refills.Keys.Any(running => running.Database == at))
                    {
                        // Answer without certifying progress across an unfinished refill.
                        await responseStream.WriteAsync(new AckFrame
                        {
                            Stable = new StableReport { Database = frontier.Database, Stable = 0, RefillRequired = cut.Contains(at) && !refills.Keys.Any(running => running.Database == at) },
                        }, stream.Token).ConfigureAwait(false);
                        continue;
                    }

                    // A frontier is an optional proof the sender repeats: one behind its database's work, or past half the
                    // bound, is dropped instead of queued.
                    if (applying.ContainsKey(at) || applying.Count >= MaxApplying / 2)
                    {
                        continue;
                    }

                    var recorded = FrontierAsync(at, origin, frontier, stream.Token);
                    applying[at] = (recorded, null, null);
                    answers.Add((at, default, recorded, _timings.Now));
                    continue;
                }

                if (frame.Refill is { } refill)
                {
                    // The sender no longer holds this machine's gap: join every bucket that differs from it, then go on from its head.
                    var tenant = (Database: Tenant(refill.Database), Incarnation: Incarnation(refill.Database));
                    if (refills.Keys.Any(running => running.Database == tenant.Database))
                    {
                        // One refill of a database at a time.
                        await RefuseAsync(responseStream, refill.Database, stream.Token).ConfigureAwait(false);
                        continue;
                    }

                    // Pinned to the copy the refill names: a replaced sender copy refuses it instead of certifying another. It
                    // starts once the database's, and the catalog's, batch or frontier in progress ended.
                    refills[tenant] = (refill, RefillAsync(After(tenant.Database), tenant.Database, hello.Machine, new Hlc(refill.Horizon), tenant.Incarnation, origin, new Hlc(refill.Coverage), stream.Token));
                    continue;
                }

                if (frame.Batch is not { } batch)
                {
                    throw new RpcException(new Status(StatusCode.InvalidArgument, "a push stream carries one hello, first"));
                }

                // A batch that names no database or incarnation cannot be refused for one: it ends the stream.
                var database = Tenant(batch.Database);
                var incarnation = Incarnation(batch.Database);
                // Any refill of the database blocks its batches: it is still joining repairs, whichever copy it names. A sender
                // awaits each batch's ack, so a second batch of a database in progress, or past the bound, is refused too.
                var busy = applying.TryGetValue(database, out var pending);
                var refilling = refills.Keys.Any(running => running.Database == database);
                if (refilling || cut.Contains(database)
                    || (pipelined ? !Admit(database, incarnation, batch, busy) : busy ? pending.Progress is not null : applying.Count >= MaxApplying))
                {
                    var refillRequired = cut.Contains(database) && !refilling;
                    if (pipelined)
                    {
                        // Answered after the database's batches in progress, so a sender reads its answers in order.
                        if (chains.TryGetValue(database, out var broken))
                        {
                            broken.Broken = true;
                        }

                        answers.Add((database, default, RefuseAfterAsync(busy ? pending.Done : Task.CompletedTask, batch.Database, refillRequired), received));
                        continue;
                    }

                    await RefuseAsync(responseStream, batch.Database, stream.Token, refillRequired).ConfigureAwait(false);
                    continue;
                }

                // A chained batch follows its predecessor, which it applies after; one it follows that was refused refuses it.
                var previous = pipelined ? (incarnation, batch.After) : last.GetValueOrDefault(database);
                var predecessor = pipelined && busy && pending.Progress is not null ? pending.Done : null;
                var progress = new ApplyProgress();
                var queued = new Queued(batch, incarnation, received, _memory.TryReserve(MemoryLane.Replication, BatchBytes(batch), tenant: null), Catalog(database));
                var joined = ApplyAsync(After(database), predecessor, queued, database, previous, origin, region, progress, stream.Token);
                if (predecessor is not null && pending.Batch is { } before)
                {
                    // The batch it follows may still take it into its own transaction.
                    before.Next = queued;
                }

                applying[database] = (joined, progress, queued);
                working.Add((joined, progress, batch.Database, batch.LastChangeId));
                answers.Add((database, incarnation, joined, received));
            }
        }
        catch (OperationCanceledException) when (stream.IsCancellationRequested && !context.CancellationToken.IsCancellationRequested)
        {
            // A newer stream of the same origin took over, or this machine stops.
        }
        catch (Exception error) when (silent.IsCancellationRequested)
        {
            _logger.ZLogWarning(error, $"The replication stream from machine {origin.Value:@Origin} in {region:@Region} was silent past the progress timeout; it is ended");
        }
        finally
        {
            // A refill ends with its stream; one not acknowledged leaves its database cut for the origin's next stream.
            await CancelPreviousAsync(stream, ended.Task).ConfigureAwait(false);
            if (precedingStop is not null)
            {
                // Also when cancellation ended the replacement before it could stop its predecessor.
                await CancelPreviousAsync(precedingStop, precedingEnded).ConfigureAwait(false);
            }
            // A cancelled replacement still joins its predecessor before the next replacement can start.
            foreach (var pending in refills.Values.Select(refilling => (Task)refilling.Done).Concat(answers.Select(answer => answer.Done))
                .Append(reading ?? Task.CompletedTask).Append(precedingEnded))
            {
                await pending.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }

            foreach (var answer in answers.Where(answer => answer.Done.IsCompletedSuccessfully))
            {
                if (await answer.Done.ConfigureAwait(false) is { } response
                    && (response.Ack is { RefillRequired: true } || response.Stable is { RefillRequired: true }))
                {
                    _ = cut.Add(answer.Database);
                }
            }

            cut.UnionWith(refills.Keys.Select(key => key.Database));
            if (cut.Count > 0)
            {
                _cut[origin] = cut;
            }

            _ = _streams.TryRemove(new KeyValuePair<ReplicaId, (CancellationTokenSource, Task)>(origin, (stream, ended.Task)));
            ended.SetResult();
        }
    }

    public override async Task<DigestReply> Digest(DigestRequest request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        // No incarnation: discovery; a named incarnation makes the digest conditional on that copy still being held.
        var database = Tenant(request.Database);
        var expected = Incarnation(request.Database, required: false);
        var digest = database == ChangeGroups.System && !_catalogGate.Value.Trusted ? null
            : await _states.DigestAsync(database, expected, context.CancellationToken).ConfigureAwait(false);
        return digest is null
            ? new DigestReply { Held = false, CollectedBefore = 0 }
            : new DigestReply
            {
                Held = true,
                Buckets = Google.Protobuf.UnsafeByteOperations.UnsafeWrap(ApplicationEnvelope.Encode(digest.Buckets)),
                Incarnation = Google.Protobuf.ByteString.CopyFrom(digest.Incarnation.ToByteArray()),
                HeadChangeId = checked((ulong)digest.Head),
                CollectedBefore = digest.CollectedBefore.Value,
            };
    }

    public override async Task ReadBucket(BucketRequest request, IServerStreamWriter<StatePage> responseStream, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(responseStream);
        ArgumentNullException.ThrowIfNull(context);
        var database = Tenant(request.Database);
        if (database == ChangeGroups.System && !_catalogGate.Value.Trusted)
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition, "the source catalog is unverified"));
        }

        // A peer's tenant anti-entropy reads whole buckets here: bulk work, as its own rounds are.
        using var bulk = database == ChangeGroups.System ? null : StorageWork.Bulk();
        var incarnation = Incarnation(request.Database);
        if (request.Buckets.Count is 0 or > 4096 || request.Buckets.Any(bucket => bucket >= 4096))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "a bucket read names 1 to 4096 buckets, each 0 to 4095"));
        }

        using var reservation = _memory.TryReserve(MemoryLane.Replication, BucketReadBytes, tenant: null)
            ?? throw new RpcException(new Status(StatusCode.ResourceExhausted,
                "the source has no memory capacity for a bucket read"));

        try
        {
            var buckets = request.Buckets.Select(bucket => (int)bucket).ToList();
            BucketCursor? after = null;
            do
            {
                // Every page, and the end, of the incarnation the request names: a replaced copy fails the stream.
                if (await _states.ReadBucketsAsync(database, incarnation, buckets, after, ChangeGroups.MaxDeltas, ChangeGroups.MaxBatchBytes, context.CancellationToken)
                    .ConfigureAwait(false) is not (var states, var next))
                {
                    throw new RpcException(new Status(StatusCode.FailedPrecondition, "the source no longer holds the named incarnation of the database"));
                }

                after = next;
                if (states.Count > 0)
                {
                    var page = AntiEntropy.Encode(states, incarnation);
                    await responseStream.WriteAsync(page, context.CancellationToken).ConfigureAwait(false);
                    // A client database's repair page goes to its replication analytics as the requester measures it. Only
                    // anti-entropy names its region; a rebuild's certification reads are not replication traffic there either.
                    if (database != ChangeGroups.System && context.RequestHeaders.GetValue(AntiEntropy.RegionHeader) is { } requester)
                    {
                        _usage.RecordReplicationBytes(database, requester, "anti_entropy", sent: true, page.CalculateSize());
                    }
                }
            }
            while (after is not null);

            // Recheck the answering state source without scanning its digests again.
            if (await _states.ReadBucketsAsync(database, incarnation, [], null, 1, 1, context.CancellationToken).ConfigureAwait(false) is null)
            {
                throw new RpcException(new Status(StatusCode.FailedPrecondition, "the source no longer holds the named incarnation of the database"));
            }
        }
        catch (Exception error) when (error is StorageBusyException or QosMemoryException)
        {
            throw new RpcException(new Status(StatusCode.ResourceExhausted,
                "the source bucket read exceeded memory or page capacity"));
        }
    }

    // A refill that fails (the named machine unreachable, a deadline) refuses the database: the sender asks again later.
    private async Task<int?> RefillAsync(Task before, TenantContext database, string machine, Hlc horizon, Ulid incarnation, ReplicaId origin, Hlc coverage, CancellationToken cancellationToken)
    {
        try
        {
            // Refuse excess work immediately; a queue or an admission wait here would grow with reconnecting databases.
            if (Interlocked.Increment(ref _refilling) > MaxRefills)
            {
                return null;
            }

            using var reservation = _memory.TryReserve(MemoryLane.Bulk, RefillBytes, tenant: null);
            if (reservation is null)
            {
                return null;
            }

            // Waiting on the catalog is admitted work too; excess refills must be refused before that wait.
            await before.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            if (!before.IsCompletedSuccessfully)
            {
                return null;
            }

            if (await _sink.RecordFrontierAsync(database, origin, default, default, null, cancellationToken).ConfigureAwait(false) is not { } local)
            {
                return null;
            }

            var repaired = await _antiEntropy.RefillAsync(database, machine, horizon, incarnation, cancellationToken).ConfigureAwait(false);
            if (repaired is null)
            {
                return null;
            }

            return await _sink.RecordFrontierAsync(database, origin, coverage, default, local, cancellationToken).ConfigureAwait(false) is not null
                ? repaired : null;
        }
        catch (Exception error) when (!cancellationToken.IsCancellationRequested)
        {
            if (database == ChangeGroups.System
                || await _faults.RefusedAsync(ChangeGroups.System, Ulid.Empty, cancellationToken).ConfigureAwait(false))
            {
                throw;
            }

            _logger.ZLogWarning(error, $"A refill from machine {machine:@Peer} failed; the database is refused until its sender asks again");
            return null;
        }
        finally
        {
            _ = Interlocked.Decrement(ref _refilling);
        }
    }

    // The batch's bytes were reserved when it arrived and are held until it applied, not until its ack is written; a batch
    // without them is refused and sent again. One applied by the batch before it returns the answer that batch gave it.
    private async Task<AckFrame?> ApplyAsync(
        Task before, Task<AckFrame?>? predecessor, Queued queued, TenantContext database, (Ulid Incarnation, ulong ChangeId) previous,
        ReplicaId origin, string region, ApplyProgress progress, CancellationToken cancellationToken)
    {
        var batch = queued.Batch;
        var refused = new AckFrame { Ack = new Ack { Database = batch.Database, Refused = true } };
        using var reservation = queued.Reservation;
        // Even a refusal follows its database's frontier; failed catalog work starts no dependent application.
        await before.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        if (queued.Answer is { } answered)
        {
            return answered;
        }

        if (reservation is null || !before.IsCompletedSuccessfully
            || (predecessor is not null && (await predecessor.ConfigureAwait(false))?.Ack is not { Refused: false }))
        {
            return refused;
        }

        _ = _timings.Record(ReplicationTimings.ReceiveWait, queued.Received);

        var envelope = batch.Groups;
        if (batch.Chunk is { } chunk)
        {
            try
            {
                envelope = await _chunks.FetchAsync(chunk, cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidDataException error)
            {
                // The sender sends this machine's batches inline for a while, this one included.
                _logger.ZLogWarning(error, $"A log chunk of database {database.DatabaseId:@Database} from machine {origin.Value:@Origin} in {region:@Region} could not be read; its batch is refused");
                refused.Ack.ChunkUnread = true;
                return refused;
            }
        }

        List<(Queued Batch, List<OriginGroup> Groups)> taken;
        try
        {
            taken = [(queued, queued.Decoded ?? Decode(database, queued.Incarnation, batch, envelope, previous))];
        }
        catch (InvalidDataException error)
        {
            // Nothing of the batch applies and its database is refused: the sender holds that database, and the stream
            // goes on with the others.
            _logger.ZLogError(error, $"A batch of database {database.DatabaseId:@Database} from machine {origin.Value:@Origin} in {region:@Region} could not be decoded; it is refused");
            _undecodable.Add(1, new KeyValuePair<string, object?>("peer_region", region));
            return refused;
        }

        Take(taken, database);
        var groups = taken.Count == 1 ? taken[0].Groups : [.. taken.SelectMany(item => item.Groups)];
        Hlc? applied;
        Hlc stableAt = default;
        try
        {
            // The highest coverage any of them expects, checked before any joins: at most stricter than one at a time.
            applied = await _sink.ApplyAsync(database, origin, groups, new Hlc(taken.Max(item => item.Batch.Batch.Expect)), cancellationToken, progress).ConfigureAwait(false);
            if (applied is not null)
            {
                var stable = _timings.Now;
                stableAt = await _states.StableAsync(database, cancellationToken).ConfigureAwait(false);
                _ = _timings.Record(ReplicationTimings.ReceiveStable, stable);
            }
        }
        catch (ReceiverCoverageException)
        {
            refused.Ack.RefillRequired = true;
            return Refuse(taken, refused);
        }
        catch (Exception error) when (!cancellationToken.IsCancellationRequested)
        {
            if (database == ChangeGroups.System
                || await _faults.RefusedAsync(ChangeGroups.System, Ulid.Empty, cancellationToken).ConfigureAwait(false))
            {
                throw;
            }

            // A batch that fails to apply here (a stored state that no longer decodes, the disk) refuses its database
            // only: the sender sends it again later, and the other databases go on.
            _logger.ZLogError(error, $"A batch of database {database.DatabaseId:@Database} from machine {origin.Value:@Origin} in {region:@Region} could not be applied; it is refused");
            return Refuse(taken, refused);
        }

        if (applied is null)
        {
            return Refuse(taken, refused);
        }

        Observe(database, batch, taken[0].Groups, region);
        foreach (var (follower, own) in taken.Skip(1))
        {
            Observe(database, follower.Batch, own, region);
            follower.Answer = Applied(follower.Batch, own, stableAt);
        }

        return Applied(batch, taken[0].Groups, stableAt);
    }

    // Takes the batches queued behind the first into its transaction, in their chain, while they were reserved and past the
    // catalog frame they follow, decode, and keep the whole within one batch's groups and bytes and one transaction's deltas.
    // The first one that does not fit stops it and applies on its own turn, with the groups decoded here.
    private void Take(List<(Queued Batch, List<OriginGroup> Groups)> taken, TenantContext database)
    {
        var first = taken[0];
        if (first.Batch.Batch.Chunk is not null)
        {
            return;
        }

        var (count, deltas, bytes) = (first.Groups.Count, first.Groups.Sum(group => group.Deltas.Count), (long)ApplicationEnvelope.DecodedLength(first.Batch.Batch.Groups.Span));
        for (var next = first.Batch.Next; next is { Reservation: not null } && next.Incarnation == first.Batch.Incarnation && next.Catalog.IsCompletedSuccessfully && next.Batch.Chunk is null; next = next.Next)
        {
            bytes += ApplicationEnvelope.DecodedLength(next.Batch.Groups.Span);
            if (bytes > ChangeGroups.MaxBatchBytes)
            {
                return;
            }

            try
            {
                next.Decoded ??= Decode(database, next.Incarnation, next.Batch, next.Batch.Groups, (next.Incarnation, next.Batch.After));
            }
            catch (InvalidDataException)
            {
                // Refused on its own turn, which logs and counts it.
                return;
            }

            (count, deltas) = (count + next.Decoded.Count, deltas + next.Decoded.Sum(group => group.Deltas.Count));
            if (count > ChangeGroups.MaxGroups || deltas > ChangeGroups.MaxDeltasOf(database))
            {
                return;
            }

            _ = _timings.Record(ReplicationTimings.ReceiveWait, next.Received);
            taken.Add((next, next.Decoded));
        }
    }

    // The batches taken behind the first are refused with it, as a refusal refuses every batch after it in its chain.
    private static AckFrame Refuse(List<(Queued Batch, List<OriginGroup> Groups)> taken, AckFrame refused)
    {
        foreach (var (follower, _) in taken.Skip(1))
        {
            follower.Answer = new AckFrame { Ack = new Ack { Database = follower.Batch.Database, Refused = true } };
        }

        return refused;
    }

    private static AckFrame Applied(Batch batch, List<OriginGroup> groups, Hlc stable) => new()
    {
        Ack = new Ack
        {
            Database = batch.Database,
            ChangeId = batch.LastChangeId,
            Applied = groups.Max(group => group.Deltas.Max(delta => delta.Stamp.Value)),
            Stable = stable.Value,
        },
    };

    private static List<OriginGroup> Decode(TenantContext database, Ulid incarnation, Batch batch, ByteString envelope, (Ulid Incarnation, ulong ChangeId) previous)
    {
        if (previous.Incarnation == incarnation && batch.LastChangeId <= previous.ChangeId)
        {
            throw new InvalidDataException("a stream's batches of one database have growing change ids");
        }

        var groups = ChangeGroups.Decode(database, envelope, batch.LastChangeId);
        return previous.Incarnation == incarnation && groups[0].ChangeId <= (long)previous.ChangeId
            ? throw new InvalidDataException("a stream's groups of one database have growing change ids")
            : groups;
    }

    // Null when the frontier is refused: no batch is in flight for it, so the report is suppressed instead of a refused ack.
    private async Task<AckFrame?> FrontierAsync(TenantContext database, ReplicaId origin, Frontier frontier, CancellationToken cancellationToken)
    {
        Hlc stable;
        try
        {
            if (await _sink.RecordFrontierAsync(database, origin, new Hlc(frontier.Hlc), new Hlc(frontier.Expect), null, cancellationToken).ConfigureAwait(false) is null)
            {
                return null;
            }
            stable = await _states.StableAsync(database, cancellationToken).ConfigureAwait(false);
        }
        catch (ReceiverCoverageException)
        {
            return new AckFrame { Stable = new StableReport { Database = frontier.Database, RefillRequired = true } };
        }
        catch (Exception error) when (!cancellationToken.IsCancellationRequested)
        {
            if (database == ChangeGroups.System
                || await _faults.RefusedAsync(ChangeGroups.System, Ulid.Empty, cancellationToken).ConfigureAwait(false))
            {
                throw;
            }

            _logger.ZLogWarning(error, $"The frontier of database {database.DatabaseId:@Database} from machine {origin.Value:@Origin} is refused; the stream continues with other databases");
            return null;
        }

        return new AckFrame { Stable = new StableReport { Database = frontier.Database, Stable = stable.Value } };
    }

    // The wire bytes, then the decompressed, parsed and decoded groups of the size the envelope declares (Decode refuses more).
    // A chunk's sealed and opened bytes take the place of the wire bytes.
    private static long BatchBytes(Batch batch) => batch.Chunk is { } chunk
        ? (2L * Math.Min(chunk.Length, (uint)ReplicationProtocol.MaxMessageBytes)) + (3L * Math.Min(chunk.Decoded, (uint)ReplicationProtocol.MaxApplicationBytes))
        : batch.Groups.Length + (3L * Math.Min(ApplicationEnvelope.DecodedLength(batch.Groups.Span), ReplicationProtocol.MaxApplicationBytes));

    private async Task CancelPreviousAsync(CancellationTokenSource stop, Task ended)
    {
        try
        {
            await stop.CancelAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException) when (ended.IsCompleted)
        {
            // A completed predecessor can dispose its token before the replacement requests cancellation.
        }
        catch (AggregateException error)
        {
            // Cancellation already took effect; callback failures must not skip joining work or publishing its cut.
            _logger.ZLogWarning(error, $"A peer stream's cancellation callback failed; its outstanding work is still joined");
        }
    }

    private static Task RefuseAsync(IServerStreamWriter<AckFrame> responseStream, V1.Database database, CancellationToken cancellationToken, bool refillRequired = false) =>
        responseStream.WriteAsync(new AckFrame { Ack = new Ack { Database = database, Refused = true, RefillRequired = refillRequired } }, cancellationToken);

    private static async Task<AckFrame?> RefuseAfterAsync(Task before, V1.Database database, bool refillRequired)
    {
        await before.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        return new AckFrame { Ack = new Ack { Database = database, Refused = true, RefillRequired = refillRequired } };
    }

    private static TenantContext Tenant(V1.Database? database)
    {
        try
        {
            return ChangeGroups.Tenant(database);
        }
        catch (InvalidDataException error)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, error.Message));
        }
    }

    // The incarnation of the origin's copy a frame names; required unless it is a discovery.
    private static Ulid Incarnation(V1.Database? database) => Incarnation(database, required: true)!.Value;

    private static Ulid? Incarnation(V1.Database? database, bool required)
    {
        try
        {
            return required ? ChangeGroups.RequiredIncarnation(database) : ChangeGroups.Incarnation(database);
        }
        catch (InvalidDataException error)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, error.Message));
        }
    }

    // One database's chain of a protocol 2 sender's batches on one stream: the incarnation they name, the change id the
    // newest applied one ended at (or the chain's start), the one the next batch must follow, the batches in progress, and
    // whether a refusal broke it.
    private sealed class Chain(Ulid incarnation, ulong start)
    {
        public Ulid Incarnation { get; } = incarnation;

        public ulong Applied { get; set; } = start;

        public ulong Expected { get; set; } = start;

        public int Pending { get; set; }

        public bool Broken { get; set; }
    }

    // A protocol 2 batch admitted on a stream, with the memory reserved for it on arrival and the catalog frame it follows.
    private sealed class Queued(Batch batch, Ulid incarnation, long received, IMemoryReservation? reservation, Task catalog)
    {
        public Batch Batch { get; } = batch;

        public Ulid Incarnation { get; } = incarnation;

        public long Received { get; } = received;

        public IMemoryReservation? Reservation { get; } = reservation;

        public Task Catalog { get; } = catalog;

        // The batch admitted after it in its chain: linked by the stream loop while this one may already be applying.
        public Queued? Next { get => Volatile.Read(ref field); set => Volatile.Write(ref field, value); }

        // Its groups, when the batch before it decoded them but could not take it.
        public List<OriginGroup>? Decoded { get; set; }

        // Its answer, when the batch before it applied it in its own transaction; read once that batch's answer is done.
        public AckFrame? Answer { get; set; }
    }

    // A client database's applied batch also goes to its replication analytics, never to its billed operations: a batch
    // sent again after a lost ack counts again.
    private void Observe(TenantContext database, Batch batch, List<OriginGroup> groups, string region)
    {
        var tag = new KeyValuePair<string, object?>("peer_region", region);
        var now = _time.GetUtcNow().ToUnixTimeMilliseconds();
        _merged.Add(groups.Count, tag);
        var deltas = 0;
        foreach (var group in groups)
        {
            var stamp = group.Deltas.Max(delta => delta.Stamp.Milliseconds);
            _delay.Record(Math.Max(0, now - stamp) / 1000.0, tag);
            deltas += group.Deltas.Count;
        }

        if (database != ChangeGroups.System)
        {
            _usage.RecordReplicationBytes(database, region, "delta", sent: false, batch.CalculateSize());
            _usage.RecordReplicatedOperations(database, region, deltas);
        }
    }
}

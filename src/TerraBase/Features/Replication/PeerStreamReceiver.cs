using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Grpc.Core;
using TerraBase.Configuration;
using TerraBase.Engine;
using TerraBase.Engine.Lattices;
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
/// </summary>
internal sealed class PeerStreamReceiver : V1.Replication.ReplicationBase
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
    private int _refilling;

    // A test observes a replacement while its predecessor's cancelled repair is still ending.
    internal Func<ReplicaId, CancellationToken, Task>? Replacing { get; set; }

    private readonly Lazy<ICatalogGate> _catalogGate;

    public PeerStreamReceiver(
        IChangeGroupSink sink, IReplicaStates states, AntiEntropy antiEntropy, LocalReplica self, TimeProvider time, IMeterFactory meters, IHostApplicationLifetime lifetime,
        ILogger<PeerStreamReceiver> logger, IMemoryBudget memory, Lazy<ICatalogGate> catalogGate, IReplicaCopyFaults faults, ReplicationTimings timings)
    {
        _timings = timings;
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
            advice: new InstrumentAdvice<double> { HistogramBucketBoundaries = [0.05, 0.1, 0.25, 0.5, 1, 2.5, 5, 10, 30, 60, 300] });
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
        var applying = new Dictionary<TenantContext, (Task<AckFrame?> Done, ApplyProgress? Progress)>();
        // Every batch in progress with its work, reported on heartbeats: a pipelined database has several, applied in turn.
        var working = new List<(Task<AckFrame?> Done, ApplyProgress Progress, V1.Database Wire, ulong ChangeId)>();
        // Batches and frontiers in arrival order. A database's frames run one after another, so they also complete in this order.
        var answers = new List<(TenantContext Database, Ulid Incarnation, Task<AckFrame?> Done, long Received)>();
        // A client database's batch or refill also follows the catalog's frame in progress: the sender lists the system
        // database first, and a database it registers is refused until that registration commits.
        Task After(TenantContext database)
        {
            Task own = applying.TryGetValue(database, out var pending) ? pending.Done : Task.CompletedTask;
            Task catalog = database != ChangeGroups.System && applying.TryGetValue(ChangeGroups.System, out var system)
                ? system.Done : Task.CompletedTask;
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
            await responseStream.WriteAsync(new AckFrame { Welcome = new Welcome { Node = _self.Id.Value, Protocol = ReplicationProtocol.Version } }, stream.Token).ConfigureAwait(false);
            reading = requestStream.MoveNext(stream.Token);
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
                var received = _timings.Now;
                silent.CancelAfter(ReplicationProtocol.ProgressTimeout);
                reading = requestStream.MoveNext(stream.Token);
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
                    applying[at] = (recorded, null);
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
                var joined = ApplyAsync(After(database), predecessor, received, database, incarnation, previous, origin, batch, region, progress, stream.Token);
                applying[database] = (joined, progress);
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
                    await responseStream.WriteAsync(AntiEntropy.Encode(states, incarnation), context.CancellationToken).ConfigureAwait(false);
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

    // Reserves the batch's bytes before decoding it and holds them until it applied, not until its ack is written; a batch
    // without them is refused and sent again.
    private async Task<AckFrame?> ApplyAsync(
        Task before, Task<AckFrame?>? predecessor, long received, TenantContext database, Ulid incarnation, (Ulid Incarnation, ulong ChangeId) previous,
        ReplicaId origin, Batch batch, string region, ApplyProgress progress, CancellationToken cancellationToken)
    {
        var refused = new AckFrame { Ack = new Ack { Database = batch.Database, Refused = true } };
        using var reservation = _memory.TryReserve(MemoryLane.Replication, BatchBytes(batch), tenant: null);
        // Even a refusal follows its database's frontier; failed catalog work starts no dependent application.
        await before.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        if (reservation is null || !before.IsCompletedSuccessfully
            || (predecessor is not null && (await predecessor.ConfigureAwait(false))?.Ack is not { Refused: false }))
        {
            return refused;
        }

        _ = _timings.Record(ReplicationTimings.ReceiveWait, received);

        List<OriginGroup> groups;
        try
        {
            if (previous.Incarnation == incarnation && batch.LastChangeId <= previous.ChangeId)
            {
                throw new InvalidDataException("a stream's batches of one database have growing change ids");
            }

            groups = ChangeGroups.Decode(database, batch.Groups, batch.LastChangeId);
            if (previous.Incarnation == incarnation && groups[0].ChangeId <= (long)previous.ChangeId)
            {
                throw new InvalidDataException("a stream's groups of one database have growing change ids");
            }
        }
        catch (InvalidDataException error)
        {
            // Nothing of the batch applies and its database is refused: the sender holds that database, and the stream
            // goes on with the others.
            _logger.ZLogError(error, $"A batch of database {database.DatabaseId:@Database} from machine {origin.Value:@Origin} in {region:@Region} could not be decoded; it is refused");
            _undecodable.Add(1, new KeyValuePair<string, object?>("peer_region", region));
            return refused;
        }

        Hlc? applied;
        Hlc stableAt = default;
        try
        {
            applied = await _sink.ApplyAsync(database, origin, groups, new Hlc(batch.Expect), cancellationToken, progress).ConfigureAwait(false);
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
            return refused;
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
            return refused;
        }

        if (applied is not { } appliedAt)
        {
            return refused;
        }

        Observe(groups, region);
        return new AckFrame
        {
            Ack = new Ack { Database = batch.Database, ChangeId = batch.LastChangeId, Applied = appliedAt.Value, Stable = stableAt.Value },
        };
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
    private static long BatchBytes(Batch batch) =>
        batch.Groups.Length + (3L * Math.Min(ApplicationEnvelope.DecodedLength(batch.Groups.Span), ReplicationProtocol.MaxApplicationBytes));

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

    private void Observe(List<OriginGroup> groups, string region)
    {
        var tag = new KeyValuePair<string, object?>("peer_region", region);
        var now = _time.GetUtcNow().ToUnixTimeMilliseconds();
        _merged.Add(groups.Count, tag);
        foreach (var group in groups)
        {
            var stamp = group.Deltas.Max(delta => delta.Stamp.Milliseconds);
            _delay.Record(Math.Max(0, now - stamp) / 1000.0, tag);
        }
    }
}

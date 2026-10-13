using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Grpc.Core;
using TerraBase.Configuration;
using TerraBase.Engine;
using TerraBase.Engine.Lattices;
using TerraBase.Features.Replication.V1;
using TerraBase.Hosting;
using TerraBase.Tenancy;
using ZLogger;

namespace TerraBase.Features.Replication;

/// <summary>
/// The receiving side of <c>Replication.Push</c>: joins each batch through the <see cref="IChangeGroupSink"/>, in stream
/// order, and acks it after its durable commit. A newer stream from the same origin ends the older one and starts once
/// it ended, so one origin's groups apply in order. A refill runs beside the stream: other databases' batches go on, while
/// its own database's are refused until a refill of it commits, also on the origin's next stream when this one ends first.
/// A batch that fails to apply is refused for its database only. A malformed batch ends the stream; the sender reconnects
/// and resumes from its last ack.
/// </summary>
internal sealed class PeerStreamReceiver : V1.Replication.ReplicationBase
{
    private readonly IChangeGroupSink _sink;
    private readonly IReplicaStates _states;
    private readonly AntiEntropy _antiEntropy;
    private readonly LocalReplica _self;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<ReplicaId, (CancellationTokenSource Stop, Task Ended)> _streams = new();
    // Per origin, the databases whose last refill did not commit when its stream ended.
    private readonly ConcurrentDictionary<ReplicaId, HashSet<TenantContext>> _cut = new();
    private readonly Counter<long> _merged;
    private readonly Histogram<double> _delay;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<PeerStreamReceiver> _logger;

    // A test observes a replacement while its predecessor's cancelled repair is still ending.
    internal Func<ReplicaId, CancellationToken, Task>? Replacing { get; set; }

    public PeerStreamReceiver(
        IChangeGroupSink sink, IReplicaStates states, AntiEntropy antiEntropy, LocalReplica self, TimeProvider time, IMeterFactory meters, IHostApplicationLifetime lifetime,
        ILogger<PeerStreamReceiver> logger)
    {
        _logger = logger;
        _sink = sink;
        _states = states;
        _antiEntropy = antiEntropy;
        _lifetime = lifetime;
        _self = self;
        _time = time;
        var meter = meters.Create(Telemetry.MeterName);
        _merged = meter.CreateCounter<long>("terrabase.replication.merged_groups", "{group}", "Change groups joined from peers, by the origin's region.");
        _delay = meter.CreateHistogram<double>("terrabase.replication.apply_delay", "s",
            "Apply time minus the origin stamp's physical time: convergence lag, by the origin's region.",
            advice: new InstrumentAdvice<double> { HistogramBucketBoundaries = [0.05, 0.1, 0.25, 0.5, 1, 2.5, 5, 10, 30, 60, 300] });
    }

    public override async Task Push(IAsyncStreamReader<PushFrame> requestStream, IServerStreamWriter<AckFrame> responseStream, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(requestStream);
        ArgumentNullException.ThrowIfNull(responseStream);
        ArgumentNullException.ThrowIfNull(context);
        // Shutdown ends the stream, also one still waiting for its hello; the sender resumes from its last ack elsewhere or later.
        using var stream = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken, _lifetime.ApplicationStopping);
        Hello hello;
        try
        {
            if (!await requestStream.MoveNext(stream.Token).ConfigureAwait(false) || requestStream.Current.Hello is not { } first)
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument, "a push stream starts with a hello"));
            }

            hello = first;
        }
        catch (OperationCanceledException) when (stream.IsCancellationRequested && !context.CancellationToken.IsCancellationRequested)
        {
            return;
        }

        var origin = new ReplicaId(hello.Node);
        if (origin == _self.Id || hello.Node == 0)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "a push stream comes from another machine"));
        }

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
        var last = new Dictionary<TenantContext, ulong>();
        // The databases a refill runs for, and those whose last refill failed: their batches are refused until a refill of
        // them commits, so none joins before the repairs it follows.
        var refills = new Dictionary<TenantContext, (Refill Frame, Task<int?> Done)>();
        HashSet<TenantContext> cut = [];
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
            await responseStream.WriteAsync(new AckFrame { Welcome = new Welcome { Node = _self.Id.Value } }, stream.Token).ConfigureAwait(false);
            reading = requestStream.MoveNext(stream.Token);
            while (true)
            {
                // A refill may take minutes: it runs beside the frames, so other databases' batches are still acked in time.
                _ = await Task.WhenAny([reading, .. refills.Values.Select(refilling => refilling.Done)]).ConfigureAwait(false);
                foreach (var (tenant, (asked, done)) in refills.Where(entry => entry.Value.Done.IsCompleted).ToList())
                {
                    var refilled = await done.ConfigureAwait(false) is not null;
                    _ = refills.Remove(tenant);
                    if (refilled)
                    {
                        _ = cut.Remove(tenant);
                        last[tenant] = asked.ChangeId;
                    }
                    else
                    {
                        _ = cut.Add(tenant);
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
                reading = requestStream.MoveNext(stream.Token);
                if (frame.KindCase == PushFrame.KindOneofCase.Heartbeat)
                {
                    continue;
                }

                if (frame.Refill is { } refill)
                {
                    // The sender no longer holds this machine's gap: join every bucket that differs from it, then go on from its head.
                    var tenant = Tenant(refill.Database);
                    if (refills.ContainsKey(tenant))
                    {
                        // One refill of a database at a time.
                        await RefuseAsync(responseStream, refill.Database, stream.Token).ConfigureAwait(false);
                        continue;
                    }

                    refills[tenant] = (refill, RefillAsync(tenant, hello.Machine, stream.Token));
                    continue;
                }

                if (frame.Batch is not { } batch)
                {
                    throw new RpcException(new Status(StatusCode.InvalidArgument, "a push stream carries batches and heartbeats after its hello"));
                }

                TenantContext database;
                List<OriginGroup> groups;
                try
                {
                    database = ChangeGroups.Tenant(batch.Database);
                    if (refills.ContainsKey(database) || cut.Contains(database))
                    {
                        await RefuseAsync(responseStream, batch.Database, stream.Token).ConfigureAwait(false);
                        continue;
                    }

                    if (last.TryGetValue(database, out var previous) && batch.LastChangeId <= previous)
                    {
                        throw new InvalidDataException("a stream's batches of one database have growing change ids");
                    }

                    groups = ChangeGroups.Decode(batch.Groups, batch.LastChangeId);
                    if (groups[0].ChangeId <= (long)previous)
                    {
                        throw new InvalidDataException("a stream's groups of one database have growing change ids");
                    }
                }
                catch (InvalidDataException error)
                {
                    throw new RpcException(new Status(StatusCode.InvalidArgument, error.Message));
                }

                Hlc? applied;
                try
                {
                    applied = await _sink.ApplyAsync(database, origin, groups, stream.Token).ConfigureAwait(false);
                }
                catch (Exception error) when (!stream.IsCancellationRequested)
                {
                    // A batch that fails to apply here (a stored state that no longer decodes, the disk) refuses its database
                    // only: the sender sends it again later, and the other databases go on.
                    _logger.ZLogError(error, $"A batch of database {database.DatabaseId:@Database} from machine {origin.Value:@Origin} in {region:@Region} could not be applied; it is refused");
                    applied = null;
                }

                if (applied is { } stamp)
                {
                    last[database] = batch.LastChangeId;
                    Observe(groups, region);
                }

                await responseStream.WriteAsync(new AckFrame
                {
                    Ack = new Ack
                    {
                        Database = batch.Database,
                        ChangeId = applied is null ? 0 : batch.LastChangeId,
                        Applied = applied?.Value ?? 0,
                        Refused = applied is null,
                    },
                }, stream.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stream.IsCancellationRequested && !context.CancellationToken.IsCancellationRequested)
        {
            // A newer stream of the same origin took over, or this machine stops.
        }
        finally
        {
            // A refill ends with its stream; one not acknowledged leaves its database cut for the origin's next stream.
            await stream.CancelAsync().ConfigureAwait(false);
            if (precedingStop is not null)
            {
                // Also when cancellation ended the replacement before it could stop its predecessor.
                await CancelPreviousAsync(precedingStop, precedingEnded).ConfigureAwait(false);
            }
            // A cancelled replacement still joins its predecessor before the next replacement can start.
            foreach (var pending in refills.Values.Select(refilling => (Task)refilling.Done).Append(reading ?? Task.CompletedTask).Append(precedingEnded))
            {
                await pending.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }

            cut.UnionWith(refills.Keys);
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
        var digests = await _states.DigestAsync(Tenant(request.Database), context.CancellationToken).ConfigureAwait(false);
        return digests is null
            ? new DigestReply { Held = false }
            : new DigestReply { Held = true, Buckets = Google.Protobuf.UnsafeByteOperations.UnsafeWrap(ApplicationEnvelope.Encode(digests)) };
    }

    public override async Task ReadBucket(BucketRequest request, IServerStreamWriter<StatePage> responseStream, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(responseStream);
        ArgumentNullException.ThrowIfNull(context);
        var database = Tenant(request.Database);
        if (request.Buckets.Count is 0 or > 4096 || request.Buckets.Any(bucket => bucket >= 4096))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "a bucket read names 1 to 4096 buckets, each 0 to 4095"));
        }

        var buckets = request.Buckets.Select(bucket => (int)bucket).ToList();
        BucketCursor? after = null;
        do
        {
            (var states, after) = await _states.ReadBucketsAsync(database, buckets, after, ChangeGroups.MaxDeltas, ChangeGroups.MaxBatchBytes, context.CancellationToken)
                .ConfigureAwait(false);
            if (states.Count > 0)
            {
                await responseStream.WriteAsync(AntiEntropy.Encode(states), context.CancellationToken).ConfigureAwait(false);
            }
        }
        while (after is not null);

        // An unavailable database also reads as an empty page; its stream must not certify complete coverage.
        if (await _states.DigestAsync(database, context.CancellationToken).ConfigureAwait(false) is null)
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition, "the source no longer holds the database"));
        }
    }

    // A refill that fails (the named machine unreachable, a deadline) refuses the database: the sender asks again later.
    private async Task<int?> RefillAsync(TenantContext database, string machine, CancellationToken cancellationToken)
    {
        try
        {
            return await _antiEntropy.RefillAsync(database, machine, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.ZLogWarning(error, $"A refill from machine {machine:@Peer} failed; the database is refused until its sender asks again");
            return null;
        }
    }

    private static async Task CancelPreviousAsync(CancellationTokenSource stop, Task ended)
    {
        try
        {
            await stop.CancelAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException) when (ended.IsCompleted)
        {
            // A completed predecessor can dispose its token before the replacement requests cancellation.
        }
    }

    private static Task RefuseAsync(IServerStreamWriter<AckFrame> responseStream, V1.Database database, CancellationToken cancellationToken) =>
        responseStream.WriteAsync(new AckFrame { Ack = new Ack { Database = database, Refused = true } }, cancellationToken);

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

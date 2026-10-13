using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Grpc.Core;
using TerraBase.Configuration;
using TerraBase.Engine;
using TerraBase.Engine.Lattices;
using TerraBase.Features.Replication.V1;
using TerraBase.Hosting;
using TerraBase.Tenancy;

namespace TerraBase.Features.Replication;

/// <summary>
/// The receiving side of <c>Replication.Push</c>: joins each batch through the <see cref="IChangeGroupSink"/>, in stream
/// order, and acks it after its durable commit. A newer stream from the same origin ends the older one, so one origin's
/// groups apply in order. A malformed batch ends the stream; the sender reconnects and resumes from its last ack.
/// </summary>
internal sealed class PeerStreamReceiver : V1.Replication.ReplicationBase
{
    private readonly IChangeGroupSink _sink;
    private readonly IReplicaStates _states;
    private readonly AntiEntropy _antiEntropy;
    private readonly LocalReplica _self;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<ReplicaId, CancellationTokenSource> _streams = new();
    private readonly Counter<long> _merged;
    private readonly Histogram<double> _delay;
    private readonly IHostApplicationLifetime _lifetime;

    public PeerStreamReceiver(
        IChangeGroupSink sink, IReplicaStates states, AntiEntropy antiEntropy, LocalReplica self, TimeProvider time, IMeterFactory meters, IHostApplicationLifetime lifetime)
    {
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
        if (!await requestStream.MoveNext(context.CancellationToken).ConfigureAwait(false) || requestStream.Current.Hello is not { } hello)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "a push stream starts with a hello"));
        }

        var origin = new ReplicaId(hello.Node);
        if (origin == _self.Id || hello.Node == 0)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "a push stream comes from another machine"));
        }

        var region = TerraBaseOptions.IsRegion(hello.Region) ? hello.Region : "unknown";
        // Shutdown ends the stream; the sender resumes from its last ack elsewhere or later.
        using var stream = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken, _lifetime.ApplicationStopping);
        if (_streams.TryGetValue(origin, out var older))
        {
            await older.CancelAsync().ConfigureAwait(false);
        }

        _streams[origin] = stream;
        try
        {
            await responseStream.WriteAsync(new AckFrame { Welcome = new Welcome { Node = _self.Id.Value } }, stream.Token).ConfigureAwait(false);
            var last = new Dictionary<TenantContext, ulong>();
            while (await requestStream.MoveNext(stream.Token).ConfigureAwait(false))
            {
                var frame = requestStream.Current;
                if (frame.KindCase == PushFrame.KindOneofCase.Heartbeat)
                {
                    continue;
                }

                if (frame.Refill is { } refill)
                {
                    // The sender no longer holds this machine's gap: join every bucket that differs from it, then go on from its head.
                    var refilled = await _antiEntropy.RefillAsync(Tenant(refill.Database), hello.Machine, stream.Token).ConfigureAwait(false);
                    if (refilled is not null)
                    {
                        last[Tenant(refill.Database)] = refill.ChangeId;
                    }

                    await responseStream.WriteAsync(new AckFrame
                    {
                        Ack = new Ack { Database = refill.Database, ChangeId = refilled is null ? 0 : refill.ChangeId, Refused = refilled is null },
                    }, stream.Token).ConfigureAwait(false);
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

                var applied = await _sink.ApplyAsync(database, origin, groups, stream.Token).ConfigureAwait(false);
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
            _ = _streams.TryRemove(new KeyValuePair<ReplicaId, CancellationTokenSource>(origin, stream));
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

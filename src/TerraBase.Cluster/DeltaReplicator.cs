using System.Collections.Concurrent;
using System.Globalization;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using TerraScale.Contracts.Quic;
using TerraScale.QuicRpc;

namespace TerraBase.Cluster;

internal closed record class OutboundMessage(Ulid DatabaseId);

internal sealed record class OutboundDelta(Ulid DatabaseId, DeltaPushMessage Message) : OutboundMessage(DatabaseId);

internal sealed record class OutboundStrongCommit(Ulid DatabaseId, StrongCommitMessage Message) : OutboundMessage(DatabaseId);

internal sealed record class OutboundStrongRelease(Ulid DatabaseId, StrongWoundMessage Message) : OutboundMessage(DatabaseId);

internal sealed record class OutboundDeltaAck(Ulid DatabaseId, DeltaAckMessage Message) : OutboundMessage(DatabaseId);

public readonly union RemoteReplicationOutcome(RemoteApplyOutcome, DatabaseNotFound, SchemaMismatch, StorageCorrupted, StorageBusy, ClusterUnavailable);

public sealed class DeltaReplicator(ClusterTopology topology, QuicRpcClient client, ClusterDatabases databases, ChangefeedPublisher changefeed, StrongLockTable locks, TimeProvider timeProvider, ILogger<DeltaReplicator> logger) : IAsyncDisposable
{
    private const int OutboundCapacity = 16384;

    private readonly Lock _outboxGate = new();
    private readonly ConcurrentDictionary<string, PeerOutbox> _outboxes = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<(Ulid DatabaseId, string NodeId), CausalBookmark> _acknowledged = new();
    private readonly CancellationTokenSource _shutdown = new();
    private long _dropped;
    private long _applied;
    private int _disposed;

    public long DroppedPushes => Interlocked.Read(ref _dropped);

    public long AppliedRemoteCommits => Interlocked.Read(ref _applied);

    public CausalBookmark Acknowledged(Ulid databaseId, string nodeId) => _acknowledged.GetValueOrDefault((databaseId, nodeId), CausalBookmark.Empty);

    public void Map(QuicRpcHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        _ = host.MapPush<DeltaPushMessage>(QuicMessageType.DeltaPush, HandleDeltaPushAsync);
        _ = host.MapPush<DeltaAckMessage>(QuicMessageType.DeltaAck, HandleDeltaAckAsync);
        _ = host.MapPush<StrongCommitMessage>(QuicMessageType.StrongCommit, HandleStrongCommitAsync);
        _ = host.MapPush<StrongWoundMessage>(QuicMessageType.StrongWound, HandleStrongReleaseAsync);
    }

    public void PublishFast(Ulid databaseId, CommitMetadata commit, bool ackRequested)
    {
        ArgumentNullException.ThrowIfNull(commit);
        if (commit.Sequence == 0)
        {
            return;
        }

        var message = new DeltaPushMessage(databaseId, topology.LocalNodeId, [commit], ackRequested);
        var peers = topology.RegionPeers.Count < 2 ? topology.Peers : topology.RemotePeers;
        foreach (var peer in peers)
        {
            Enqueue(peer.NodeId, new OutboundDelta(databaseId, message));
        }
    }

    public void BroadcastStrongCommit(Ulid databaseId, CommitMetadata commit)
    {
        ArgumentNullException.ThrowIfNull(commit);
        var message = new StrongCommitMessage(new StrongCommitPayload(databaseId, commit), topology.LocalNodeId);
        foreach (var peer in topology.Peers)
        {
            Enqueue(peer.NodeId, new OutboundStrongCommit(databaseId, message));
        }
    }

    public void BroadcastStrongRelease(Ulid databaseId, Ulid txId, Ulid woundedBy, HlcTimestamp winnerPriority, IEnumerable<string> nodeIds)
    {
        ArgumentNullException.ThrowIfNull(nodeIds);
        var message = new StrongWoundMessage(new StrongWoundPayload(databaseId, txId, woundedBy, winnerPriority));
        foreach (var nodeId in nodeIds.Distinct(StringComparer.Ordinal))
        {
            Enqueue(nodeId, new OutboundStrongRelease(databaseId, message));
        }
    }

    public async ValueTask<RemoteReplicationOutcome> ApplyAsync(Ulid databaseId, IReadOnlyList<CommitMetadata> commits, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(commits);
        var resolved = await databases.ResolveAsync(databaseId, cancellationToken);
        return resolved switch
        {
            ISqlDatabaseSession session => await ApplyLeasedAsync(session, commits, cancellationToken),
            DatabaseNotFound notFound => notFound,
            ClusterUnavailable unavailable => unavailable,
        };
    }

    public ValueTask<RemoteReplicationOutcome> ApplyToSessionAsync(ISqlDatabaseSession session, IReadOnlyList<CommitMetadata> commits, CancellationToken cancellationToken) =>
        ApplyToSessionAsync(session, commits, requestCatchUpOnBuffered: false, cancellationToken);

    public async ValueTask<RemoteReplicationOutcome> ApplyToSessionAsync(ISqlDatabaseSession session, IReadOnlyList<CommitMetadata> commits, bool requestCatchUpOnBuffered, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(commits);
        var foreign = commits.Where(commit => !string.Equals(commit.OriginRegion, topology.StorageOrigin, StringComparison.Ordinal) && commit.Sequence > 0).ToArray();
        if (foreign.Length == 0)
        {
            return new RemoteApplyOutcome(0, commits.Count, 0, session.CurrentBookmark);
        }

        var before = session.CurrentBookmark;
        var applied = await session.ApplyRemoteBatchAsync(new RemoteCommitBatch(session.DatabaseId, foreign), cancellationToken);
        return applied switch
        {
            RemoteApplyOutcome outcome => await AppliedAsync(session.DatabaseId, foreign, before, outcome, requestCatchUpOnBuffered, cancellationToken),
            SchemaMismatch mismatch => mismatch,
            StorageCorrupted corrupted => corrupted,
            StorageBusy busy => busy,
        };
    }

    public async ValueTask DisposeAsync()
    {
        ICollection<PeerOutbox> outboxes;
        lock (_outboxGate)
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            outboxes = _outboxes.Values;
        }

        await _shutdown.CancelAsync();
        foreach (var outbox in outboxes)
        {
            outbox.Queue.Writer.TryComplete();
        }

        await Task.WhenAll(outboxes.Select(static outbox => outbox.Pump));
        _shutdown.Dispose();
    }

    private async ValueTask<RemoteReplicationOutcome> ApplyLeasedAsync(ISqlDatabaseSession session, IReadOnlyList<CommitMetadata> commits, CancellationToken cancellationToken)
    {
        await using (session)
        {
            return await ApplyToSessionAsync(session, commits, requestCatchUpOnBuffered: true, cancellationToken);
        }
    }

    private async ValueTask<RemoteReplicationOutcome> AppliedAsync(Ulid databaseId, CommitMetadata[] commits, CausalBookmark before, RemoteApplyOutcome outcome, bool requestCatchUpOnBuffered, CancellationToken cancellationToken)
    {
        databases.Observe(databaseId, outcome.Bookmark);
        Interlocked.Add(ref _applied, outcome.Applied);
        foreach (var commit in commits)
        {
            if (commit.Mode == ConsistencyMode.Strong && commit.Sequence <= outcome.Bookmark.Get(commit.OriginRegion))
            {
                _ = locks.Release(databaseId, commit.TxId);
            }
        }

        if (changefeed.IsEnabled)
        {
            foreach (var commit in commits.OrderBy(static commit => commit.Hlc))
            {
                if (commit.Sequence > before.Get(commit.OriginRegion) && commit.Sequence <= outcome.Bookmark.Get(commit.OriginRegion))
                {
                    await changefeed.PublishAsync(databaseId, commit, cancellationToken);
                }
            }
        }

        if (requestCatchUpOnBuffered && outcome.Buffered > 0)
        {
            _ = databases.RequestCatchUp(databaseId, FrontierOf(commits));
        }

        return outcome;
    }

    private async ValueTask<PushDispatchOutcome> HandleDeltaPushAsync(QuicCallContext context, DeltaPushMessage message, CancellationToken cancellationToken)
    {
        if (!topology.IsPeer(context, message.SenderNodeId) || message.Commits.Any(commit => !topology.IsPeerOrigin(context, commit.OriginRegion)))
        {
            return ClusterTopology.PeerForbidden();
        }

        var applied = await ApplyAsync(message.DatabaseId, message.Commits, cancellationToken);
        var bookmark = applied switch
        {
            RemoteApplyOutcome outcome => outcome.Bookmark,
            DatabaseNotFound or SchemaMismatch or StorageCorrupted or StorageBusy or ClusterUnavailable => null,
        };
        if (bookmark is null)
        {
            ClusterLog.RemoteApplyDeferred(logger, message.DatabaseId, context.RemoteNodeId, Describe(applied));
            _ = databases.RequestCatchUp(message.DatabaseId, FrontierOf(message.Commits));
            return new QuicSent();
        }

        if (message.AckRequested)
        {
            Enqueue(context.RemoteNodeId, new OutboundDeltaAck(message.DatabaseId, new DeltaAckMessage(message.DatabaseId, topology.LocalNodeId, bookmark)));
        }

        return new QuicSent();
    }

    private ValueTask<PushDispatchOutcome> HandleDeltaAckAsync(QuicCallContext context, DeltaAckMessage message, CancellationToken cancellationToken)
    {
        if (!topology.IsPeer(context, message.NodeId))
        {
            return ValueTask.FromResult<PushDispatchOutcome>(ClusterTopology.PeerForbidden());
        }

        _ = _acknowledged.AddOrUpdate((message.DatabaseId, context.RemoteNodeId), message.Bookmark, (_, existing) => existing.Merge(message.Bookmark));
        return ValueTask.FromResult<PushDispatchOutcome>(new QuicSent());
    }

    private async ValueTask<PushDispatchOutcome> HandleStrongCommitAsync(QuicCallContext context, StrongCommitMessage message, CancellationToken cancellationToken)
    {
        var payload = message.Payload;
        if (!topology.IsPeer(context, message.SenderNodeId) || payload.Commit.Mode != ConsistencyMode.Strong
            || !topology.IsPeerOrigin(context, payload.Commit.OriginRegion))
        {
            return ClusterTopology.PeerForbidden();
        }

        var applied = await ApplyAsync(payload.DatabaseId, [payload.Commit], cancellationToken);
        var failure = applied switch
        {
            RemoteApplyOutcome => null,
            DatabaseNotFound or SchemaMismatch or StorageCorrupted or StorageBusy or ClusterUnavailable => Describe(applied),
        };
        if (failure is not null)
        {
            ClusterLog.RemoteApplyDeferred(logger, payload.DatabaseId, context.RemoteNodeId, failure);
            _ = databases.RequestCatchUp(payload.DatabaseId, FrontierOf([payload.Commit]));
        }

        return new QuicSent();
    }

    private ValueTask<PushDispatchOutcome> HandleStrongReleaseAsync(QuicCallContext context, StrongWoundMessage message, CancellationToken cancellationToken)
    {
        var payload = message.Payload;
        if (!topology.IsPeer(context) || payload.WoundedBy != payload.TxId)
        {
            return ValueTask.FromResult<PushDispatchOutcome>(ClusterTopology.PeerForbidden());
        }

        locks.AbortFrom(payload.DatabaseId, payload.TxId, context.RemoteNodeId);
        return ValueTask.FromResult<PushDispatchOutcome>(new QuicSent());
    }

    private static CausalBookmark FrontierOf(IReadOnlyList<CommitMetadata> commits) =>
        CausalBookmark.Create(commits.SelectMany(static commit => commit.Sequence > 0
            ? commit.Bookmark.Entries.Append(KeyValuePair.Create(commit.OriginRegion, commit.Sequence))
            : commit.Bookmark.Entries));

    private static string Describe(RemoteReplicationOutcome applied) => applied switch
    {
        RemoteApplyOutcome outcome => string.Concat("applied ", outcome.Applied.ToString(CultureInfo.InvariantCulture)),
        DatabaseNotFound notFound => notFound.Message,
        SchemaMismatch mismatch => string.Concat("schema epoch mismatch: expected ", mismatch.ExpectedEpoch.ToString(CultureInfo.InvariantCulture), ", actual ", mismatch.ActualEpoch.ToString(CultureInfo.InvariantCulture)),
        StorageCorrupted corrupted => corrupted.Message,
        StorageBusy busy => busy.Message,
        ClusterUnavailable unavailable => unavailable.Message,
    };

    private void Enqueue(string nodeId, OutboundMessage message)
    {
        if (!_outboxes.TryGetValue(nodeId, out var outbox))
        {
            lock (_outboxGate)
            {
                if (Volatile.Read(ref _disposed) != 0)
                {
                    return;
                }

                outbox = _outboxes.GetOrAdd(nodeId, CreateOutbox);
            }
        }

        if (!outbox.Queue.Writer.TryWrite(message))
        {
            Interlocked.Increment(ref _dropped);
            _ = databases.RequestCatchUp(message.DatabaseId);
        }
    }

    private PeerOutbox CreateOutbox(string nodeId)
    {
        var queue = Channel.CreateBounded<OutboundMessage>(new BoundedChannelOptions(OutboundCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });
        var pump = ClusterLoop.RunAsync(
            string.Concat("push:", nodeId), token => PumpAsync(nodeId, queue.Reader, token), timeProvider, logger, _shutdown.Token);
        return new PeerOutbox(queue, pump);
    }

    private async Task PumpAsync(string nodeId, ChannelReader<OutboundMessage> reader, CancellationToken cancellationToken)
    {
        await foreach (var message in reader.ReadAllAsync(cancellationToken))
        {
            var sent = false;
            try
            {
                await SendAsync(nodeId, message, cancellationToken);
                sent = true;
            }
            finally
            {
                if (!sent && !cancellationToken.IsCancellationRequested)
                {
                    Interlocked.Increment(ref _dropped);
                    _ = databases.RequestCatchUp(message.DatabaseId);
                }
            }
        }
    }

    private async ValueTask SendAsync(string nodeId, OutboundMessage message, CancellationToken cancellationToken)
    {
        var channel = message.DatabaseId.ToString();
        var sent = message switch
        {
            OutboundDelta delta => await client.PushAsync(nodeId, QuicMessageType.DeltaPush, delta.Message, channel, cancellationToken),
            OutboundStrongCommit commit => await client.PushAsync(nodeId, QuicMessageType.StrongCommit, commit.Message, channel, cancellationToken),
            OutboundStrongRelease release => await client.PushAsync(nodeId, QuicMessageType.StrongWound, release.Message, channel, cancellationToken),
            OutboundDeltaAck ack => await client.PushAsync(nodeId, QuicMessageType.DeltaAck, ack.Message, channel, cancellationToken),
        };
        var failure = sent switch
        {
            QuicSent => null,
            QuicUnavailable unavailable => unavailable.Message,
            QuicTimeout timeout => timeout.Message,
        };
        if (failure is not null)
        {
            Interlocked.Increment(ref _dropped);
            _ = databases.RequestCatchUp(message.DatabaseId);
            ClusterLog.PushFailed(logger, nodeId, failure);
        }
    }

    private sealed record PeerOutbox(Channel<OutboundMessage> Queue, Task Pump);
}

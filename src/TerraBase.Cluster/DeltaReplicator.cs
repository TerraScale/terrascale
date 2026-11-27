using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using TerraScale.Contracts.Quic;
using TerraScale.QuicRpc;

namespace TerraBase.Cluster;

public readonly union RemoteReplicationOutcome(RemoteApplyOutcome, DatabaseNotFound, SchemaMismatch, StorageCorrupted, StorageBusy, ClusterUnavailable);

public sealed class DeltaReplicator(ClusterTopology topology, QuicRpcClient client, ClusterDatabases databases, ChangefeedPublisher changefeed, StrongLockTable locks, TimeProvider timeProvider, ILogger<DeltaReplicator> logger) : IAsyncDisposable
{
    private const int OutboundCapacity = 16384;
    private const int MaxCoalescedCommits = 64;
    private const int MaxCoalescedBytes = 1024 * 1024;

    private readonly Lock _outboxGate = new();
    private readonly ConcurrentDictionary<string, PeerOutbox> _outboxes = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<(Ulid DatabaseId, string NodeId), CausalBookmark> _acknowledged = new();
    private readonly ConcurrentDictionary<Ulid, string> _channelKeys = new();
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

        var peers = topology.RegionPeers.Count < 2 ? topology.Peers : topology.RemotePeers;
        if (peers.Count == 0)
        {
            return;
        }

        var item = OutboundItem.ForDelta(databaseId, commit, ackRequested);
        for (var index = 0; index < peers.Count; index++)
        {
            Enqueue(peers[index].NodeId, item);
        }
    }

    public void BroadcastStrongCommit(Ulid databaseId, CommitMetadata commit)
    {
        ArgumentNullException.ThrowIfNull(commit);
        var peers = topology.Peers;
        if (peers.Count == 0)
        {
            return;
        }

        var message = new StrongCommitMessage(new StrongCommitPayload(databaseId, commit), topology.LocalNodeId);
        var item = OutboundItem.ForStrongCommit(databaseId, message);
        for (var index = 0; index < peers.Count; index++)
        {
            Enqueue(peers[index].NodeId, item);
        }
    }

    public void BroadcastStrongRelease(Ulid databaseId, Ulid txId, Ulid woundedBy, HlcTimestamp winnerPriority, IEnumerable<string> nodeIds)
    {
        ArgumentNullException.ThrowIfNull(nodeIds);
        if (nodeIds is IReadOnlyCollection<string> { Count: 0 })
        {
            return;
        }

        var message = new StrongWoundMessage(new StrongWoundPayload(databaseId, txId, woundedBy, winnerPriority));
        var item = OutboundItem.ForStrongRelease(databaseId, message);
        if (nodeIds is IReadOnlyList<string> list)
        {
            if (list.Count == 1)
            {
                Enqueue(list[0], item);
                return;
            }

            HashSet<string> seen = new(list.Count, StringComparer.Ordinal);
            for (var index = 0; index < list.Count; index++)
            {
                var nodeId = list[index];
                if (seen.Add(nodeId))
                {
                    Enqueue(nodeId, item);
                }
            }

            return;
        }

        foreach (var nodeId in nodeIds.Distinct(StringComparer.Ordinal))
        {
            Enqueue(nodeId, item);
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
        var foreign = FilterForeign(commits, topology.StorageOrigin);
        if (foreign.Count == 0)
        {
            return new RemoteApplyOutcome(0, commits.Count, 0, session.CurrentBookmark, 0);
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

    private static IReadOnlyList<CommitMetadata> FilterForeign(IReadOnlyList<CommitMetadata> commits, string storageOrigin)
    {
        var count = commits.Count;
        if (count == 0)
        {
            return [];
        }

        var allForeign = true;
        for (var index = 0; index < count; index++)
        {
            var commit = commits[index];
            if (string.Equals(commit.OriginRegion, storageOrigin, StringComparison.Ordinal) || commit.Sequence <= 0)
            {
                allForeign = false;
                break;
            }
        }

        if (allForeign)
        {
            return commits;
        }

        List<CommitMetadata>? filtered = null;
        for (var index = 0; index < count; index++)
        {
            var commit = commits[index];
            if (!string.Equals(commit.OriginRegion, storageOrigin, StringComparison.Ordinal) && commit.Sequence > 0)
            {
                filtered ??= new List<CommitMetadata>(count);
                filtered.Add(commit);
            }
        }

        return filtered is not null ? filtered : [];
    }

    private async ValueTask<RemoteReplicationOutcome> ApplyLeasedAsync(ISqlDatabaseSession session, IReadOnlyList<CommitMetadata> commits, CancellationToken cancellationToken)
    {
        await using (session)
        {
            return await ApplyToSessionAsync(session, commits, requestCatchUpOnBuffered: true, cancellationToken);
        }
    }

    private async ValueTask<RemoteReplicationOutcome> AppliedAsync(Ulid databaseId, IReadOnlyList<CommitMetadata> commits, CausalBookmark before, RemoteApplyOutcome outcome, bool requestCatchUpOnBuffered, CancellationToken cancellationToken)
    {
        databases.Observe(databaseId, outcome.Bookmark);
        Interlocked.Add(ref _applied, outcome.Applied);
        if (outcome.Quarantined > 0)
        {
            ClusterLog.RemoteCommitsQuarantined(logger, databaseId, outcome.Quarantined, outcome.Bookmark);
        }

        for (var index = 0; index < commits.Count; index++)
        {
            var commit = commits[index];
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
        if (!topology.IsPeer(context, message.SenderNodeId))
        {
            return ClusterTopology.PeerForbidden();
        }

        var commits = message.Commits;
        for (var index = 0; index < commits.Count; index++)
        {
            if (!topology.IsPeerOrigin(context, commits[index].OriginRegion))
            {
                return ClusterTopology.PeerForbidden();
            }
        }

        var applied = await ApplyAsync(message.DatabaseId, commits, cancellationToken);
        var bookmark = applied switch
        {
            RemoteApplyOutcome outcome => outcome.Bookmark,
            DatabaseNotFound or SchemaMismatch or StorageCorrupted or StorageBusy or ClusterUnavailable => null,
        };
        if (bookmark is null)
        {
            ClusterLog.RemoteApplyDeferred(logger, message.DatabaseId, context.RemoteNodeId, Describe(applied));
            _ = databases.RequestCatchUp(message.DatabaseId, FrontierOf(commits));
            return new QuicSent();
        }

        if (message.AckRequested)
        {
            Enqueue(context.RemoteNodeId, OutboundItem.ForDeltaAck(message.DatabaseId, bookmark));
        }

        return new QuicSent();
    }

    private ValueTask<PushDispatchOutcome> HandleDeltaAckAsync(QuicCallContext context, DeltaAckMessage message, CancellationToken cancellationToken)
    {
        if (!topology.IsPeer(context, message.NodeId))
        {
            return ValueTask.FromResult<PushDispatchOutcome>(ClusterTopology.PeerForbidden());
        }

        _ = _acknowledged.AddOrUpdate(
            (message.DatabaseId, context.RemoteNodeId),
            static (_, incoming) => incoming,
            static (_, existing, incoming) => existing.Dominates(incoming) ? existing : incoming.Dominates(existing) ? incoming : existing.Merge(incoming),
            message.Bookmark);
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

        CommitMetadata[] single = [payload.Commit];
        var applied = await ApplyAsync(payload.DatabaseId, single, cancellationToken);
        var failure = applied switch
        {
            RemoteApplyOutcome => null,
            DatabaseNotFound or SchemaMismatch or StorageCorrupted or StorageBusy or ClusterUnavailable => Describe(applied),
        };
        if (failure is not null)
        {
            ClusterLog.RemoteApplyDeferred(logger, payload.DatabaseId, context.RemoteNodeId, failure);
            _ = databases.RequestCatchUp(payload.DatabaseId, FrontierOf(single));
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

    private string ChannelKey(Ulid databaseId) =>
        _channelKeys.GetOrAdd(databaseId, static id => id.ToString());

    private void Enqueue(string nodeId, in OutboundItem item)
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

        if (!outbox.Queue.Writer.TryWrite(item))
        {
            Interlocked.Increment(ref _dropped);
            _ = databases.RequestCatchUp(item.DatabaseId);
        }
    }

    private PeerOutbox CreateOutbox(string nodeId)
    {
        var queue = Channel.CreateBounded<OutboundItem>(new BoundedChannelOptions(OutboundCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });
        var pump = ClusterLoop.RunAsync(
            string.Concat("push:", nodeId), token => PumpAsync(nodeId, queue.Reader, token), timeProvider, logger, _shutdown.Token);
        return new PeerOutbox(queue, pump);
    }

    private async Task PumpAsync(string nodeId, ChannelReader<OutboundItem> reader, CancellationToken cancellationToken)
    {
        List<CommitMetadata> deltaBatch = new(MaxCoalescedCommits);
        while (await reader.WaitToReadAsync(cancellationToken))
        {
            while (reader.TryRead(out var item))
            {
                var sent = false;
                try
                {
                    if (item.Kind == OutboundKind.Delta && item.Commit is { } firstCommit)
                    {
                        deltaBatch.Clear();
                        deltaBatch.Add(firstCommit);
                        var batchBytes = firstCommit.DeltaPayload.Length;
                        var ackRequested = item.AckRequested;
                        while (deltaBatch.Count < MaxCoalescedCommits
                            && reader.TryPeek(out var next)
                            && next.Kind == OutboundKind.Delta
                            && next.DatabaseId == item.DatabaseId
                            && next.Commit is { } nextCommit)
                        {
                            var nextBytes = nextCommit.DeltaPayload.Length;
                            if (batchBytes + nextBytes > MaxCoalescedBytes)
                            {
                                break;
                            }

                            _ = reader.TryRead(out next);
                            deltaBatch.Add(nextCommit);
                            batchBytes += nextBytes;
                            ackRequested |= next.AckRequested;
                        }

                        var push = new DeltaPushMessage(item.DatabaseId, topology.LocalNodeId, deltaBatch, ackRequested);
                        await SendPushAsync(nodeId, item.DatabaseId, QuicMessageType.DeltaPush, push, cancellationToken);
                        deltaBatch.Clear();
                        sent = true;
                        continue;
                    }

                    if (item.Kind == OutboundKind.DeltaAck && item.Bookmark is { } firstBookmark)
                    {
                        var merged = firstBookmark;
                        while (reader.TryPeek(out var next)
                            && next.Kind == OutboundKind.DeltaAck
                            && next.DatabaseId == item.DatabaseId
                            && next.Bookmark is { } nextBookmark)
                        {
                            _ = reader.TryRead(out next);
                            if (!merged.Dominates(nextBookmark))
                            {
                                merged = nextBookmark.Dominates(merged) ? nextBookmark : merged.Merge(nextBookmark);
                            }
                        }

                        var ack = new DeltaAckMessage(item.DatabaseId, topology.LocalNodeId, merged);
                        await SendPushAsync(nodeId, item.DatabaseId, QuicMessageType.DeltaAck, ack, cancellationToken);
                        sent = true;
                        continue;
                    }

                    await SendControlAsync(nodeId, item, cancellationToken);
                    sent = true;
                }
                finally
                {
                    deltaBatch.Clear();
                    if (!sent && !cancellationToken.IsCancellationRequested)
                    {
                        Interlocked.Increment(ref _dropped);
                        _ = databases.RequestCatchUp(item.DatabaseId);
                    }
                }
            }
        }
    }

    private ValueTask SendControlAsync(string nodeId, in OutboundItem item, CancellationToken cancellationToken) => item.Kind switch
    {
        OutboundKind.StrongCommit when item.StrongCommit is { } commit => SendPushAsync(nodeId, item.DatabaseId, QuicMessageType.StrongCommit, commit, cancellationToken),
        OutboundKind.StrongRelease when item.StrongRelease is { } release => SendPushAsync(nodeId, item.DatabaseId, QuicMessageType.StrongWound, release, cancellationToken),
        _ => throw new UnreachableException(),
    };

    private async ValueTask SendPushAsync<TMessage>(string nodeId, Ulid databaseId, ushort messageType, TMessage message, CancellationToken cancellationToken)
        where TMessage : IBinaryMessage<TMessage>
    {
        var channel = ChannelKey(databaseId);
        var sent = await client.PushAsync(nodeId, messageType, message, channel, cancellationToken);
        var failure = sent switch
        {
            QuicSent => null,
            QuicUnavailable unavailable => unavailable.Message,
            QuicTimeout timeout => timeout.Message,
        };
        if (failure is not null)
        {
            Interlocked.Increment(ref _dropped);
            _ = databases.RequestCatchUp(databaseId);
            ClusterLog.PushFailed(logger, nodeId, failure);
        }
    }

    private enum OutboundKind : byte
    {
        Delta = 0,
        StrongCommit = 1,
        StrongRelease = 2,
        DeltaAck = 3,
    }

    private readonly struct OutboundItem
    {
        public OutboundKind Kind { get; init; }

        public Ulid DatabaseId { get; init; }

        public CommitMetadata? Commit { get; init; }

        public StrongCommitMessage? StrongCommit { get; init; }

        public StrongWoundMessage? StrongRelease { get; init; }

        public CausalBookmark? Bookmark { get; init; }

        public bool AckRequested { get; init; }

        public static OutboundItem ForDelta(Ulid databaseId, CommitMetadata commit, bool ackRequested) => new()
        {
            Kind = OutboundKind.Delta,
            DatabaseId = databaseId,
            Commit = commit,
            AckRequested = ackRequested,
        };

        public static OutboundItem ForStrongCommit(Ulid databaseId, StrongCommitMessage message) => new()
        {
            Kind = OutboundKind.StrongCommit,
            DatabaseId = databaseId,
            StrongCommit = message,
        };

        public static OutboundItem ForStrongRelease(Ulid databaseId, StrongWoundMessage message) => new()
        {
            Kind = OutboundKind.StrongRelease,
            DatabaseId = databaseId,
            StrongRelease = message,
        };

        public static OutboundItem ForDeltaAck(Ulid databaseId, CausalBookmark bookmark) => new()
        {
            Kind = OutboundKind.DeltaAck,
            DatabaseId = databaseId,
            Bookmark = bookmark,
        };
    }

    private sealed record PeerOutbox(Channel<OutboundItem> Queue, Task Pump);
}

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

public sealed class DeltaReplicator(ClusterTopology topology, QuicRpcClient client, ClusterDatabases databases, ChangefeedPublisher changefeed, StrongLockTable locks, ILogger<DeltaReplicator> logger) : IAsyncDisposable
{
    private const int OutboundCapacity = 16384;

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
        foreach (var peer in topology.RemotePeers)
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

    public async ValueTask<Result<RemoteApplyOutcome, NotFound, SchemaMismatch, StorageCorrupted, StorageBusy, Unavailable>> ApplyAsync(Ulid databaseId, IReadOnlyList<CommitMetadata> commits, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(commits);
        var resolved = await databases.ResolveAsync(databaseId, cancellationToken).ConfigureAwait(false);
        return resolved switch
        {
            ISqlDatabaseSession session => await ApplyLeasedAsync(session, commits, cancellationToken).ConfigureAwait(false),
            NotFound notFound => notFound,
            Unavailable unavailable => unavailable,
        };
    }

    public async ValueTask<Result<RemoteApplyOutcome, NotFound, SchemaMismatch, StorageCorrupted, StorageBusy, Unavailable>> ApplyToSessionAsync(ISqlDatabaseSession session, IReadOnlyList<CommitMetadata> commits, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(commits);
        var foreign = commits.Where(commit => !string.Equals(commit.OriginRegion, topology.StorageOrigin, StringComparison.Ordinal) && commit.Sequence > 0).ToArray();
        if (foreign.Length == 0)
        {
            return new RemoteApplyOutcome(0, commits.Count, 0, session.CurrentBookmark);
        }

        var before = session.CurrentBookmark;
        var applied = await session.ApplyRemoteBatchAsync(new RemoteCommitBatch(session.DatabaseId, foreign), cancellationToken).ConfigureAwait(false);
        return applied switch
        {
            RemoteApplyOutcome outcome => await AppliedAsync(session.DatabaseId, foreign, before, outcome, cancellationToken).ConfigureAwait(false),
            SchemaMismatch mismatch => mismatch,
            StorageCorrupted corrupted => corrupted,
            StorageBusy busy => busy,
        };
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _shutdown.CancelAsync().ConfigureAwait(false);
        foreach (var outbox in _outboxes.Values)
        {
            outbox.Queue.Writer.TryComplete();
        }

        await Task.WhenAll(_outboxes.Values.Select(static outbox => outbox.Pump)).ConfigureAwait(false);
        _shutdown.Dispose();
    }

    private async ValueTask<Result<RemoteApplyOutcome, NotFound, SchemaMismatch, StorageCorrupted, StorageBusy, Unavailable>> ApplyLeasedAsync(ISqlDatabaseSession session, IReadOnlyList<CommitMetadata> commits, CancellationToken cancellationToken)
    {
        await using (session.ConfigureAwait(false))
        {
            return await ApplyToSessionAsync(session, commits, cancellationToken).ConfigureAwait(false);
        }
    }

    private async ValueTask<Result<RemoteApplyOutcome, NotFound, SchemaMismatch, StorageCorrupted, StorageBusy, Unavailable>> AppliedAsync(Ulid databaseId, CommitMetadata[] commits, CausalBookmark before, RemoteApplyOutcome outcome, CancellationToken cancellationToken)
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
                    await changefeed.PublishAsync(databaseId, commit, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        if (outcome.Buffered > 0)
        {
            _ = databases.RequestCatchUp(databaseId);
        }

        return outcome;
    }

    private async ValueTask HandleDeltaPushAsync(QuicCallContext context, DeltaPushMessage message, CancellationToken cancellationToken)
    {
        var applied = await ApplyAsync(message.DatabaseId, message.Commits, cancellationToken).ConfigureAwait(false);
        var bookmark = applied switch
        {
            RemoteApplyOutcome outcome => outcome.Bookmark,
            NotFound or SchemaMismatch or StorageCorrupted or StorageBusy or Unavailable => null,
        };
        if (bookmark is null)
        {
            ClusterLog.RemoteApplyDeferred(logger, message.DatabaseId, context.RemoteNodeId, Describe(applied));
            _ = databases.RequestCatchUp(message.DatabaseId);
            return;
        }

        if (message.AckRequested)
        {
            Enqueue(context.RemoteNodeId, new OutboundDeltaAck(message.DatabaseId, new DeltaAckMessage(message.DatabaseId, topology.LocalNodeId, bookmark)));
        }
    }

    private ValueTask HandleDeltaAckAsync(QuicCallContext context, DeltaAckMessage message, CancellationToken cancellationToken)
    {
        _ = _acknowledged.AddOrUpdate((message.DatabaseId, message.NodeId), message.Bookmark, (_, existing) => existing.Merge(message.Bookmark));
        return ValueTask.CompletedTask;
    }

    private async ValueTask HandleStrongCommitAsync(QuicCallContext context, StrongCommitMessage message, CancellationToken cancellationToken)
    {
        var payload = message.Payload;
        var applied = await ApplyAsync(payload.DatabaseId, [payload.Commit], cancellationToken).ConfigureAwait(false);
        _ = locks.Release(payload.DatabaseId, payload.Commit.TxId);
        var failure = applied switch
        {
            RemoteApplyOutcome => null,
            NotFound or SchemaMismatch or StorageCorrupted or StorageBusy or Unavailable => Describe(applied),
        };
        if (failure is not null)
        {
            ClusterLog.RemoteApplyDeferred(logger, payload.DatabaseId, context.RemoteNodeId, failure);
            _ = databases.RequestCatchUp(payload.DatabaseId);
        }
    }

    private ValueTask HandleStrongReleaseAsync(QuicCallContext context, StrongWoundMessage message, CancellationToken cancellationToken)
    {
        _ = locks.Release(message.Payload.DatabaseId, message.Payload.TxId);
        return ValueTask.CompletedTask;
    }

    private static string Describe(Result<RemoteApplyOutcome, NotFound, SchemaMismatch, StorageCorrupted, StorageBusy, Unavailable> applied) => applied switch
    {
        RemoteApplyOutcome outcome => string.Concat("applied ", outcome.Applied.ToString(CultureInfo.InvariantCulture)),
        NotFound notFound => notFound.Message ?? "database not found",
        SchemaMismatch mismatch => string.Concat("schema epoch mismatch: expected ", mismatch.ExpectedEpoch.ToString(CultureInfo.InvariantCulture), ", actual ", mismatch.ActualEpoch.ToString(CultureInfo.InvariantCulture)),
        StorageCorrupted corrupted => corrupted.Message,
        StorageBusy busy => busy.Message,
        Unavailable unavailable => unavailable.Message,
    };

    private void Enqueue(string nodeId, OutboundMessage message)
    {
        var outbox = _outboxes.GetOrAdd(nodeId, CreateOutbox);
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
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
            SingleWriter = false,
        });
        return new PeerOutbox(queue, PumpAsync(nodeId, queue.Reader));
    }

    private async Task PumpAsync(string nodeId, ChannelReader<OutboundMessage> reader)
    {
        var cancellationToken = _shutdown.Token;
        try
        {
            await foreach (var message in reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                await SendAsync(nodeId, message, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
    }

    private async ValueTask SendAsync(string nodeId, OutboundMessage message, CancellationToken cancellationToken)
    {
        var channel = message.DatabaseId.ToString();
        var sent = message switch
        {
            OutboundDelta delta => await client.PushAsync(nodeId, QuicMessageType.DeltaPush, delta.Message, channel, cancellationToken).ConfigureAwait(false),
            OutboundStrongCommit commit => await client.PushAsync(nodeId, QuicMessageType.StrongCommit, commit.Message, channel, cancellationToken).ConfigureAwait(false),
            OutboundStrongRelease release => await client.PushAsync(nodeId, QuicMessageType.StrongWound, release.Message, channel, cancellationToken).ConfigureAwait(false),
            OutboundDeltaAck ack => await client.PushAsync(nodeId, QuicMessageType.DeltaAck, ack.Message, channel, cancellationToken).ConfigureAwait(false),
        };
        var failure = sent switch
        {
            Done => null,
            Unavailable unavailable => unavailable.Message,
            RequestTimeout timeout => timeout.Message,
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

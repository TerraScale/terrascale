using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using TerraScale.Contracts.Quic;
using TerraScale.QuicRpc;

namespace TerraBase.Cluster;

public readonly union RemoteReplicationOutcome(RemoteApplyOutcome, DatabaseNotFound, SchemaMismatch, StorageCorrupted, StorageBusy, ClusterUnavailable);

public sealed class DeltaReplicator(ClusterTopology topology, QuicRpcClient client, ClusterDatabases databases, ChangefeedPublisher changefeed, StrongLockTable locks, TimeProvider timeProvider, ILogger<DeltaReplicator> logger) : IAsyncDisposable
{
    private const int OutboundCapacity = 32768;
    private const int MaxCoalescedCommits = 256;
    private const int MaxCoalescedBytes = 2 * 1024 * 1024;
    private const int MaxFrameBytes = QuicProtocol.MaxPayloadBytes - 4096;
    private const int MaxInboundQueuedBatches = 256;
    private const int MaxInboundBatchesPerLease = 16;
    private const int MaxCachedChannelKeys = 4096;

    private static readonly QuicSent Sent = new();
    private static readonly QuicRemoteError Forbidden = ClusterTopology.PeerForbidden();
    private static readonly Comparison<CommitMetadata> CommitOrderComparison = CompareCommits;

    private readonly Lock _outboxGate = new();
    private readonly ConcurrentDictionary<string, PeerOutbox> _outboxes = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Ulid, InboundMailbox> _inboxes = new();
    private readonly TaskCompletionSource _drainsComplete = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ConcurrentDictionary<(Ulid DatabaseId, string NodeId), CausalBookmark> _acknowledged = new();
    private readonly ConcurrentDictionary<Ulid, string> _channelKeys = new();
    private readonly string[] _fastPeerIds = ExtractNodeIds(topology.RegionPeers.Count < 2 ? topology.Peers : topology.RemotePeers);
    private readonly PeerOutbox?[] _fastPeerOutboxes = new PeerOutbox?[topology.RegionPeers.Count < 2 ? topology.Peers.Count : topology.RemotePeers.Count];
    private readonly string[] _allPeerIds = ExtractNodeIds(topology.Peers);
    private readonly PeerOutbox?[] _allPeerOutboxes = new PeerOutbox?[topology.Peers.Count];
    private readonly CancellationTokenSource _shutdown = new();
    private long _dropped;
    private long _applied;
    private int _activeDrains;
    private int _disposed;

    public long DroppedPushes => Interlocked.Read(ref _dropped);

    public long AppliedRemoteCommits => Interlocked.Read(ref _applied);

    public CausalBookmark Acknowledged(Ulid databaseId, string nodeId) => _acknowledged.GetValueOrDefault((databaseId, nodeId), CausalBookmark.Empty);

    public ValueTask FlushInboundAsync(Ulid databaseId, CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _disposed) != 0 || !_inboxes.TryGetValue(databaseId, out var mailbox))
        {
            return ValueTask.CompletedTask;
        }

        return FlushInboundCoreAsync(databaseId, mailbox, cancellationToken);
    }

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

        var peerIds = _fastPeerIds;
        if (peerIds.Length == 0)
        {
            return;
        }

        var item = OutboundItem.ForDelta(databaseId, commit, ackRequested);
        var outboxes = _fastPeerOutboxes;
        for (var index = 0; index < peerIds.Length; index++)
        {
            var outbox = Volatile.Read(ref outboxes[index]);
            if (outbox is null)
            {
                outbox = GetOrCreateOutbox(peerIds[index]);
                if (outbox is null)
                {
                    return;
                }

                Volatile.Write(ref outboxes[index], outbox);
            }

            EnqueueToOutbox(outbox, item);
        }
    }

    public void BroadcastStrongCommit(Ulid databaseId, CommitMetadata commit)
    {
        ArgumentNullException.ThrowIfNull(commit);
        var peerIds = _allPeerIds;
        if (peerIds.Length == 0)
        {
            return;
        }

        var message = new StrongCommitMessage(new StrongCommitPayload(databaseId, commit), topology.LocalNodeId);
        var item = OutboundItem.ForStrongCommit(databaseId, message);
        var outboxes = _allPeerOutboxes;
        for (var index = 0; index < peerIds.Length; index++)
        {
            var outbox = Volatile.Read(ref outboxes[index]);
            if (outbox is null)
            {
                outbox = GetOrCreateOutbox(peerIds[index]);
                if (outbox is null)
                {
                    return;
                }

                Volatile.Write(ref outboxes[index], outbox);
            }

            EnqueueToOutbox(outbox, item);
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

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<RemoteReplicationOutcome>))]
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

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<RemoteReplicationOutcome>))]
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

        if (Volatile.Read(ref _activeDrains) > 0)
        {
            await _drainsComplete.Task;
        }

        _shutdown.Dispose();
    }

    private static string[] ExtractNodeIds(IReadOnlyList<PeerNodeInfo> peers)
    {
        if (peers.Count == 0)
        {
            return [];
        }

        var ids = new string[peers.Count];
        for (var index = 0; index < peers.Count; index++)
        {
            ids[index] = peers[index].NodeId;
        }

        return ids;
    }

    private static int CompareCommits(CommitMetadata left, CommitMetadata right)
    {
        var originCompare = string.CompareOrdinal(left.OriginRegion, right.OriginRegion);
        return originCompare != 0 ? originCompare : left.Sequence.CompareTo(right.Sequence);
    }

    private static IReadOnlyList<CommitMetadata> FilterForeign(IReadOnlyList<CommitMetadata> commits, string storageOrigin)
    {
        if (commits is CommitMetadata[] array)
        {
            var arrayCount = array.Length;
            if (arrayCount == 0)
            {
                return [];
            }

            var allArrayForeign = true;
            for (var index = 0; index < arrayCount; index++)
            {
                var commit = array[index];
                if (string.Equals(commit.OriginRegion, storageOrigin, StringComparison.Ordinal) || commit.Sequence <= 0)
                {
                    allArrayForeign = false;
                    break;
                }
            }

            if (allArrayForeign)
            {
                return array;
            }

            List<CommitMetadata>? arrayFiltered = null;
            for (var index = 0; index < arrayCount; index++)
            {
                var commit = array[index];
                if (!string.Equals(commit.OriginRegion, storageOrigin, StringComparison.Ordinal) && commit.Sequence > 0)
                {
                    arrayFiltered ??= new List<CommitMetadata>(arrayCount);
                    arrayFiltered.Add(commit);
                }
            }

            return arrayFiltered is not null ? arrayFiltered : [];
        }

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

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<RemoteReplicationOutcome>))]
    private async ValueTask<RemoteReplicationOutcome> ApplyLeasedAsync(ISqlDatabaseSession session, IReadOnlyList<CommitMetadata> commits, CancellationToken cancellationToken)
    {
        await using (session)
        {
            return await ApplyToSessionAsync(session, commits, requestCatchUpOnBuffered: true, cancellationToken);
        }
    }

    private ValueTask<RemoteReplicationOutcome> AppliedAsync(Ulid databaseId, IReadOnlyList<CommitMetadata> commits, CausalBookmark before, RemoteApplyOutcome outcome, bool requestCatchUpOnBuffered, CancellationToken cancellationToken)
    {
        databases.Observe(databaseId, outcome.Bookmark);
        Interlocked.Add(ref _applied, outcome.Applied);
        if (outcome.Quarantined > 0)
        {
            ClusterLog.RemoteCommitsQuarantined(logger, databaseId, outcome.Quarantined, outcome.Bookmark);
        }

        if (locks.ActiveHolders > 0)
        {
            ReleaseStrongLocks(databaseId, commits, outcome.Bookmark);
        }

        if (changefeed.IsEnabled)
        {
            return PublishChangefeedAsync(databaseId, commits, before, outcome, requestCatchUpOnBuffered, cancellationToken);
        }

        if (requestCatchUpOnBuffered && outcome.Buffered > 0)
        {
            _ = databases.RequestCatchUp(databaseId, FrontierOf(commits));
        }

        return new(outcome);
    }

    private void ReleaseStrongLocks(Ulid databaseId, IReadOnlyList<CommitMetadata> commits, CausalBookmark bookmark)
    {
        if (commits is CommitMetadata[] array)
        {
            for (var index = 0; index < array.Length; index++)
            {
                var commit = array[index];
                if (commit.Mode == ConsistencyMode.Strong && commit.Sequence <= bookmark.Get(commit.OriginRegion))
                {
                    _ = locks.Release(databaseId, commit.TxId);
                }
            }

            return;
        }

        for (var index = 0; index < commits.Count; index++)
        {
            var commit = commits[index];
            if (commit.Mode == ConsistencyMode.Strong && commit.Sequence <= bookmark.Get(commit.OriginRegion))
            {
                _ = locks.Release(databaseId, commit.TxId);
            }
        }
    }

    private async ValueTask<RemoteReplicationOutcome> PublishChangefeedAsync(Ulid databaseId, IReadOnlyList<CommitMetadata> commits, CausalBookmark before, RemoteApplyOutcome outcome, bool requestCatchUpOnBuffered, CancellationToken cancellationToken)
    {
        foreach (var commit in commits.OrderBy(static commit => commit.Hlc))
        {
            if (commit.Sequence > before.Get(commit.OriginRegion) && commit.Sequence <= outcome.Bookmark.Get(commit.OriginRegion))
            {
                await changefeed.PublishAsync(databaseId, commit, cancellationToken);
            }
        }

        if (requestCatchUpOnBuffered && outcome.Buffered > 0)
        {
            _ = databases.RequestCatchUp(databaseId, FrontierOf(commits));
        }

        return outcome;
    }

    private ValueTask<PushDispatchOutcome> HandleDeltaPushAsync(QuicCallContext context, DeltaPushMessage message, CancellationToken cancellationToken)
    {
        if (!topology.IsPeer(context, message.SenderNodeId))
        {
            return new(Forbidden);
        }

        var commits = message.Commits;
        if (!VerifyPeerOrigins(context, commits))
        {
            return new(Forbidden);
        }

        if (commits.Count == 0)
        {
            return new(Sent);
        }

        var item = new InboundItem(context.RemoteNodeId, commits, message.AckRequested);
        if (TryEnqueueInboundFast(message.DatabaseId, item))
        {
            return new(Sent);
        }

        return EnqueueInboundSlowAsync(message.DatabaseId, item, cancellationToken);
    }

    private bool TryEnqueueInboundFast(Ulid databaseId, in InboundItem item)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return true;
        }

        while (true)
        {
            var mailbox = _inboxes.GetOrAdd(databaseId, static _ => new InboundMailbox());
            var startDrain = false;
            lock (mailbox.Gate)
            {
                if (mailbox.Removed)
                {
                    continue;
                }

                if (mailbox.Queue.Count >= MaxInboundQueuedBatches)
                {
                    return false;
                }

                mailbox.Queue.Enqueue(item);
                mailbox.EnqueuedCount++;
                if (!mailbox.Draining)
                {
                    mailbox.Draining = true;
                    startDrain = true;
                }
            }

            if (startDrain)
            {
                StartInboundDrain(databaseId, mailbox);
            }

            return true;
        }
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<PushDispatchOutcome>))]
    private async ValueTask<PushDispatchOutcome> EnqueueInboundSlowAsync(Ulid databaseId, InboundItem item, CancellationToken cancellationToken)
    {
        while (Volatile.Read(ref _disposed) == 0)
        {
            var mailbox = _inboxes.GetOrAdd(databaseId, static _ => new InboundMailbox());
            TaskCompletionSource? waitForSpace = null;
            var startDrain = false;
            lock (mailbox.Gate)
            {
                if (mailbox.Removed)
                {
                    continue;
                }

                if (mailbox.Queue.Count >= MaxInboundQueuedBatches)
                {
                    waitForSpace = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    mailbox.Waiters ??= new Queue<TaskCompletionSource>();
                    mailbox.Waiters.Enqueue(waitForSpace);
                }
                else
                {
                    mailbox.Queue.Enqueue(item);
                    mailbox.EnqueuedCount++;
                    if (!mailbox.Draining)
                    {
                        mailbox.Draining = true;
                        startDrain = true;
                    }
                }
            }

            if (waitForSpace is not null)
            {
                var woken = false;
                try
                {
                    await waitForSpace.Task.WaitAsync(cancellationToken);
                    woken = true;
                }
                finally
                {
                    if (!woken && !waitForSpace.TrySetCanceled(cancellationToken))
                    {
                        ForwardInboundWakeup(mailbox);
                    }
                }

                continue;
            }

            if (startDrain)
            {
                StartInboundDrain(databaseId, mailbox);
            }

            return Sent;
        }

        return Sent;
    }

    private void StartInboundDrain(Ulid databaseId, InboundMailbox mailbox)
    {
        _ = Interlocked.Increment(ref _activeDrains);
        if (Volatile.Read(ref _disposed) != 0)
        {
            ReleaseMailboxOnStop(databaseId, mailbox);
            EndInboundDrain();
            return;
        }

        _ = RunInboundDrainBoundaryAsync(databaseId, mailbox);
    }

    private void EndInboundDrain()
    {
        if (Interlocked.Decrement(ref _activeDrains) == 0 && Volatile.Read(ref _disposed) != 0)
        {
            _ = _drainsComplete.TrySetResult();
        }
    }

    private async Task RunInboundDrainBoundaryAsync(Ulid databaseId, InboundMailbox mailbox)
    {
        try
        {
            await DrainInboundMailboxAsync(databaseId, mailbox, _shutdown.Token);
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
            ReleaseMailboxOnStop(databaseId, mailbox);
        }
        catch (Exception exception)
        {
            ReleaseMailboxOnStop(databaseId, mailbox);
            ClusterLog.LoopFailed(logger, string.Concat("apply:", databaseId.ToString()), TimeSpan.Zero, exception);
        }
        finally
        {
            EndInboundDrain();
        }
    }

    private void ReleaseMailboxOnStop(Ulid databaseId, InboundMailbox mailbox)
    {
        Queue<TaskCompletionSource>? waiters;
        List<(long Target, TaskCompletionSource Signal)>? flushWaiters;
        lock (mailbox.Gate)
        {
            mailbox.Draining = false;
            mailbox.Removed = true;
            waiters = mailbox.Waiters;
            mailbox.Waiters = null;
            flushWaiters = mailbox.FlushWaiters;
            mailbox.FlushWaiters = null;
        }

        _ = _inboxes.TryRemove(KeyValuePair.Create(databaseId, mailbox));
        SignalWaiters(waiters);
        SignalFlushWaiters(flushWaiters);
    }

    private ValueTask FlushInboundCoreAsync(Ulid databaseId, InboundMailbox mailbox, CancellationToken cancellationToken)
    {
        while (Volatile.Read(ref _disposed) == 0)
        {
            TaskCompletionSource? signal = null;
            long target = 0;
            var removed = false;
            lock (mailbox.Gate)
            {
                if (mailbox.Removed)
                {
                    removed = true;
                }
                else
                {
                    target = mailbox.EnqueuedCount;
                    if (mailbox.AppliedCount >= target)
                    {
                        return ValueTask.CompletedTask;
                    }

                    signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    mailbox.FlushWaiters ??= [];
                    mailbox.FlushWaiters.Add((target, signal));
                }
            }

            if (removed)
            {
                if (_inboxes.TryGetValue(databaseId, out var next) && !ReferenceEquals(next, mailbox))
                {
                    mailbox = next;
                    continue;
                }

                return ValueTask.CompletedTask;
            }

            if (signal is not null)
            {
                return WaitFlushAsync(mailbox, target, signal, cancellationToken);
            }
        }

        return ValueTask.CompletedTask;
    }

    private static async ValueTask WaitFlushAsync(InboundMailbox mailbox, long target, TaskCompletionSource signal, CancellationToken cancellationToken)
    {
        var completed = false;
        try
        {
            await signal.Task.WaitAsync(cancellationToken);
            completed = true;
        }
        finally
        {
            if (!completed)
            {
                lock (mailbox.Gate)
                {
                    var waiters = mailbox.FlushWaiters;
                    if (waiters is not null)
                    {
                        for (var index = waiters.Count - 1; index >= 0; index--)
                        {
                            if (ReferenceEquals(waiters[index].Signal, signal) && waiters[index].Target == target)
                            {
                                waiters.RemoveAt(index);
                                break;
                            }
                        }
                    }
                }
            }
        }
    }

    private static void CompleteInboundBatch(InboundMailbox mailbox, long appliedCount)
    {
        TaskCompletionSource? single = null;
        List<TaskCompletionSource>? multiple = null;
        lock (mailbox.Gate)
        {
            if (appliedCount > mailbox.AppliedCount)
            {
                mailbox.AppliedCount = appliedCount;
            }

            var waiters = mailbox.FlushWaiters;
            if (waiters is not null && waiters.Count > 0)
            {
                for (var index = waiters.Count - 1; index >= 0; index--)
                {
                    if (waiters[index].Target <= mailbox.AppliedCount)
                    {
                        var ready = waiters[index].Signal;
                        waiters.RemoveAt(index);
                        if (single is null && multiple is null)
                        {
                            single = ready;
                        }
                        else
                        {
                            if (multiple is null)
                            {
                                multiple = single is { } first ? [first] : [];
                                single = null;
                            }

                            multiple.Add(ready);
                        }
                    }
                }
            }
        }

        _ = single?.TrySetResult();
        if (multiple is not null)
        {
            for (var index = 0; index < multiple.Count; index++)
            {
                _ = multiple[index].TrySetResult();
            }
        }
    }

    private async Task DrainInboundMailboxAsync(Ulid databaseId, InboundMailbox mailbox, CancellationToken cancellationToken)
    {
        List<CommitMetadata>? combined = null;
        List<string>? ackPeers = null;

        while (!cancellationToken.IsCancellationRequested)
        {
            if (!TryDequeueInboundBatch(databaseId, mailbox, ref combined, ref ackPeers, out var batch, out var singleSender, out var singleAck, out var batchEnd))
            {
                return;
            }

            var resolved = await databases.ResolveAsync(databaseId, cancellationToken);
            var (session, resolveFailure) = UnpackResolved(resolved);
            if (session is null)
            {
                CompleteInboundBatch(mailbox, batchEnd);
                ClusterLog.RemoteApplyDeferred(logger, databaseId, singleSender, resolveFailure);
                _ = databases.RequestCatchUp(databaseId, FrontierOf(batch));
                combined?.Clear();
                ackPeers?.Clear();
                continue;
            }

            await using (session)
            {
                var leasesUsed = 0;
                while (true)
                {
                    var applied = await ApplyToSessionAsync(session, batch, requestCatchUpOnBuffered: true, cancellationToken);
                    CompleteInboundBatch(mailbox, batchEnd);
                    var bookmark = UnpackAppliedBookmark(applied);
                    if (bookmark is null)
                    {
                        ClusterLog.RemoteApplyDeferred(logger, databaseId, singleSender, Describe(applied));
                        _ = databases.RequestCatchUp(databaseId, FrontierOf(batch));
                    }
                    else
                    {
                        EnqueueInboundAcks(databaseId, bookmark, singleSender, singleAck, ackPeers);
                    }

                    combined?.Clear();
                    ackPeers?.Clear();
                    leasesUsed++;
                    if (cancellationToken.IsCancellationRequested || leasesUsed >= MaxInboundBatchesPerLease)
                    {
                        break;
                    }

                    if (!TryDequeueInboundBatch(databaseId, mailbox, ref combined, ref ackPeers, out batch, out singleSender, out singleAck, out batchEnd))
                    {
                        return;
                    }
                }
            }
        }

        ReleaseMailboxOnStop(databaseId, mailbox);
    }

    private static (ISqlDatabaseSession? Session, string Failure) UnpackResolved(DatabaseResolveOutcome resolved) => resolved switch
    {
        ISqlDatabaseSession leased => (leased, string.Empty),
        DatabaseNotFound notFound => (null, notFound.Message),
        ClusterUnavailable unavailable => (null, unavailable.Message),
    };

    private static CausalBookmark? UnpackAppliedBookmark(RemoteReplicationOutcome applied) => applied switch
    {
        RemoteApplyOutcome outcome => outcome.Bookmark,
        DatabaseNotFound or SchemaMismatch or StorageCorrupted or StorageBusy or ClusterUnavailable => null,
    };

    private bool TryDequeueInboundBatch(
        Ulid databaseId,
        InboundMailbox mailbox,
        ref List<CommitMetadata>? combined,
        ref List<string>? ackPeers,
        out IReadOnlyList<CommitMetadata> batch,
        out string primarySender,
        out bool singleAck,
        out long batchEnd)
    {
        var outOfOrder = false;
        var empty = false;
        Queue<TaskCompletionSource>? drainWaiters = null;
        List<(long Target, TaskCompletionSource Signal)>? flushWaiters = null;
        TaskCompletionSource? singleWaiter = null;
        List<TaskCompletionSource>? multiWaiters = null;

        lock (mailbox.Gate)
        {
            if (mailbox.Queue.Count == 0)
            {
                mailbox.Draining = false;
                mailbox.Removed = true;
                mailbox.AppliedCount = mailbox.EnqueuedCount;
                _ = _inboxes.TryRemove(KeyValuePair.Create(databaseId, mailbox));
                drainWaiters = mailbox.Waiters;
                mailbox.Waiters = null;
                flushWaiters = mailbox.FlushWaiters;
                mailbox.FlushWaiters = null;
                empty = true;
                batch = [];
                primarySender = string.Empty;
                singleAck = false;
                batchEnd = mailbox.EnqueuedCount;
            }
            else
            {
                var first = mailbox.Queue.Dequeue();
                var dequeued = 1;
                primarySender = first.SenderNodeId;
                singleAck = first.AckRequested;

                if (mailbox.Queue.Count == 0)
                {
                    batch = first.Commits;
                    mailbox.DequeuedCount += dequeued;
                    batchEnd = mailbox.DequeuedCount;
                    PopWaitersLocked(mailbox, dequeued, ref singleWaiter, ref multiWaiters);
                }
                else
                {
                    combined ??= new List<CommitMetadata>(MaxCoalescedCommits);
                    combined.Clear();
                    var batchBytes = AppendCommits(combined, first.Commits, ref outOfOrder);
                    if (singleAck)
                    {
                        ackPeers ??= [];
                        ackPeers.Clear();
                        ackPeers.Add(first.SenderNodeId);
                    }

                    while (mailbox.Queue.Count > 0 && combined.Count < MaxCoalescedCommits && batchBytes < MaxCoalescedBytes)
                    {
                        var peeked = mailbox.Queue.Peek();
                        if (combined.Count + peeked.Commits.Count > MaxCoalescedCommits)
                        {
                            break;
                        }

                        var next = mailbox.Queue.Dequeue();
                        dequeued++;
                        batchBytes += AppendCommits(combined, next.Commits, ref outOfOrder);
                        if (next.AckRequested)
                        {
                            ackPeers ??= [];
                            AddDistinctPeer(ackPeers, next.SenderNodeId);
                        }
                    }

                    batch = combined;
                    mailbox.DequeuedCount += dequeued;
                    batchEnd = mailbox.DequeuedCount;
                    PopWaitersLocked(mailbox, dequeued, ref singleWaiter, ref multiWaiters);
                }
            }
        }

        if (empty)
        {
            SignalWaiters(drainWaiters);
            SignalFlushWaiters(flushWaiters);
            return false;
        }

        WakeWaiters(mailbox, singleWaiter, multiWaiters);

        if (outOfOrder && combined is not null)
        {
            combined.Sort(CommitOrderComparison);
        }

        return true;
    }

    private static int AppendCommits(List<CommitMetadata> target, IReadOnlyList<CommitMetadata> source, ref bool outOfOrder)
    {
        var bytes = 0;
        CommitMetadata? previous = target.Count > 0 ? target[^1] : null;
        if (source is CommitMetadata[] array)
        {
            for (var index = 0; index < array.Length; index++)
            {
                var commit = array[index];
                if (previous is not null && !outOfOrder && CompareCommits(commit, previous) < 0)
                {
                    outOfOrder = true;
                }

                target.Add(commit);
                bytes += commit.DeltaPayload.Length;
                previous = commit;
            }

            return bytes;
        }

        for (var index = 0; index < source.Count; index++)
        {
            var commit = source[index];
            if (previous is not null && !outOfOrder && CompareCommits(commit, previous) < 0)
            {
                outOfOrder = true;
            }

            target.Add(commit);
            bytes += commit.DeltaPayload.Length;
            previous = commit;
        }

        return bytes;
    }

    private static void AddDistinctPeer(List<string> peers, string nodeId)
    {
        for (var index = 0; index < peers.Count; index++)
        {
            if (string.Equals(peers[index], nodeId, StringComparison.Ordinal))
            {
                return;
            }
        }

        peers.Add(nodeId);
    }

    private static void PopWaitersLocked(InboundMailbox mailbox, int count, ref TaskCompletionSource? single, ref List<TaskCompletionSource>? multiple)
    {
        var waiters = mailbox.Waiters;
        if (waiters is null || waiters.Count == 0 || count <= 0)
        {
            return;
        }

        var popped = 0;
        while (popped < count && waiters.TryDequeue(out var waiter))
        {
            if (waiter.Task.IsCompleted)
            {
                continue;
            }

            if (popped == 0)
            {
                single = waiter;
            }
            else
            {
                if (multiple is null)
                {
                    multiple = new List<TaskCompletionSource>(Math.Min(count, waiters.Count + 2));
                    if (single is not null)
                    {
                        multiple.Add(single);
                        single = null;
                    }
                }

                multiple.Add(waiter);
            }

            popped++;
        }
    }

    private static void WakeWaiters(InboundMailbox mailbox, TaskCompletionSource? single, List<TaskCompletionSource>? multiple)
    {
        var missed = 0;
        if (single is not null && !single.TrySetResult())
        {
            missed++;
        }

        if (multiple is not null)
        {
            for (var index = 0; index < multiple.Count; index++)
            {
                if (!multiple[index].TrySetResult())
                {
                    missed++;
                }
            }
        }

        for (var index = 0; index < missed; index++)
        {
            ForwardInboundWakeup(mailbox);
        }
    }

    private static void ForwardInboundWakeup(InboundMailbox mailbox)
    {
        while (true)
        {
            TaskCompletionSource? next = null;
            lock (mailbox.Gate)
            {
                var waiters = mailbox.Waiters;
                if (waiters is null)
                {
                    return;
                }

                while (waiters.TryDequeue(out var candidate))
                {
                    if (!candidate.Task.IsCompleted)
                    {
                        next = candidate;
                        break;
                    }
                }
            }

            if (next is null || next.TrySetResult())
            {
                return;
            }
        }
    }

    private static void SignalWaiters(Queue<TaskCompletionSource>? waiters)
    {
        if (waiters is null)
        {
            return;
        }

        while (waiters.TryDequeue(out var waiter))
        {
            _ = waiter.TrySetResult();
        }
    }

    private static void SignalFlushWaiters(List<(long Target, TaskCompletionSource Signal)>? waiters)
    {
        if (waiters is null)
        {
            return;
        }

        for (var index = 0; index < waiters.Count; index++)
        {
            _ = waiters[index].Signal.TrySetResult();
        }
    }

    private void EnqueueInboundAcks(Ulid databaseId, CausalBookmark bookmark, string singleSender, bool singleAck, List<string>? ackPeers)
    {
        if (ackPeers is { Count: > 0 })
        {
            var item = OutboundItem.ForDeltaAck(databaseId, bookmark);
            for (var index = 0; index < ackPeers.Count; index++)
            {
                Enqueue(ackPeers[index], item);
            }

            return;
        }

        if (singleAck)
        {
            Enqueue(singleSender, OutboundItem.ForDeltaAck(databaseId, bookmark));
        }
    }

    private bool VerifyPeerOrigins(QuicCallContext context, IReadOnlyList<CommitMetadata> commits)
    {
        string? verifiedOrigin = null;
        if (commits is CommitMetadata[] array)
        {
            for (var index = 0; index < array.Length; index++)
            {
                var origin = array[index].OriginRegion;
                if (!ReferenceEquals(origin, verifiedOrigin)
                    && !string.Equals(origin, verifiedOrigin, StringComparison.Ordinal))
                {
                    if (!topology.IsPeerOrigin(context, origin))
                    {
                        return false;
                    }

                    verifiedOrigin = origin;
                }
            }

            return true;
        }

        for (var index = 0; index < commits.Count; index++)
        {
            var origin = commits[index].OriginRegion;
            if (!ReferenceEquals(origin, verifiedOrigin)
                && !string.Equals(origin, verifiedOrigin, StringComparison.Ordinal))
            {
                if (!topology.IsPeerOrigin(context, origin))
                {
                    return false;
                }

                verifiedOrigin = origin;
            }
        }

        return true;
    }

    private ValueTask<PushDispatchOutcome> HandleDeltaAckAsync(QuicCallContext context, DeltaAckMessage message, CancellationToken cancellationToken)
    {
        if (!topology.IsPeer(context, message.NodeId))
        {
            return new(Forbidden);
        }

        _ = _acknowledged.AddOrUpdate(
            (message.DatabaseId, context.RemoteNodeId),
            static (_, incoming) => incoming,
            static (_, existing, incoming) => existing.Dominates(incoming) ? existing : incoming.Dominates(existing) ? incoming : existing.Merge(incoming),
            message.Bookmark);
        return new(Sent);
    }

    private async ValueTask<PushDispatchOutcome> HandleStrongCommitAsync(QuicCallContext context, StrongCommitMessage message, CancellationToken cancellationToken)
    {
        var payload = message.Payload;
        if (!topology.IsPeer(context, message.SenderNodeId) || payload.Commit.Mode != ConsistencyMode.Strong
            || !topology.IsPeerOrigin(context, payload.Commit.OriginRegion))
        {
            return Forbidden;
        }

        await FlushInboundAsync(payload.DatabaseId, cancellationToken);
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

        return Sent;
    }

    private ValueTask<PushDispatchOutcome> HandleStrongReleaseAsync(QuicCallContext context, StrongWoundMessage message, CancellationToken cancellationToken)
    {
        var payload = message.Payload;
        if (!topology.IsPeer(context) || payload.WoundedBy != payload.TxId)
        {
            return new(Forbidden);
        }

        locks.AbortFrom(payload.DatabaseId, payload.TxId, context.RemoteNodeId);
        return new(Sent);
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

    private string ChannelKey(Ulid databaseId)
    {
        if (_channelKeys.TryGetValue(databaseId, out var existing))
        {
            return existing;
        }

        if (_channelKeys.Count >= MaxCachedChannelKeys)
        {
            _channelKeys.Clear();
        }

        return _channelKeys.GetOrAdd(databaseId, databaseId.ToString());
    }

    private void Enqueue(string nodeId, in OutboundItem item)
    {
        var outbox = GetOrCreateOutbox(nodeId);
        if (outbox is null)
        {
            return;
        }

        EnqueueToOutbox(outbox, item);
    }

    private PeerOutbox? GetOrCreateOutbox(string nodeId)
    {
        if (_outboxes.TryGetValue(nodeId, out var outbox))
        {
            return outbox;
        }

        lock (_outboxGate)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return null;
            }

            return _outboxes.GetOrAdd(nodeId, CreateOutbox);
        }
    }

    private void EnqueueToOutbox(PeerOutbox outbox, in OutboundItem item)
    {
        if (!outbox.Queue.Writer.TryWrite(item) && Volatile.Read(ref _disposed) == 0)
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
        var localNodeId = topology.LocalNodeId;
        var baseFrameOverhead = 16 + 5 + (localNodeId.Length * 3) + 5 + 1;
        List<CommitMetadata> deltaBatch = new(MaxCoalescedCommits);
        Ulid lastDbId = default;
        string? lastChannel = null;

        while (await reader.WaitToReadAsync(cancellationToken))
        {
            while (reader.TryRead(out var item))
            {
                var sent = false;
                try
                {
                    string channel;
                    if (lastChannel is not null && item.DatabaseId == lastDbId)
                    {
                        channel = lastChannel;
                    }
                    else
                    {
                        channel = ChannelKey(item.DatabaseId);
                        lastDbId = item.DatabaseId;
                        lastChannel = channel;
                    }

                    if (item.Kind == OutboundKind.Delta && item.Commit is { } firstCommit)
                    {
                        deltaBatch.Clear();
                        deltaBatch.Add(firstCommit);
                        var batchBytes = firstCommit.DeltaPayload.Length;
                        var estimatedFrameBytes = baseFrameOverhead + ClusterCodec.EstimateCommitSize(firstCommit);
                        var ackRequested = item.AckRequested;
                        var previous = firstCommit;
                        var outOfOrder = false;
                        while (deltaBatch.Count < MaxCoalescedCommits
                            && reader.TryPeek(out var next)
                            && next.Kind == OutboundKind.Delta
                            && next.DatabaseId == item.DatabaseId
                            && next.Commit is { } nextCommit)
                        {
                            var nextBytes = nextCommit.DeltaPayload.Length;
                            var nextEstimated = ClusterCodec.EstimateCommitSize(nextCommit);
                            if (batchBytes + nextBytes > MaxCoalescedBytes || estimatedFrameBytes + nextEstimated > MaxFrameBytes)
                            {
                                break;
                            }

                            _ = reader.TryRead(out next);
                            if (!outOfOrder && CompareCommits(nextCommit, previous) < 0)
                            {
                                outOfOrder = true;
                            }

                            deltaBatch.Add(nextCommit);
                            previous = nextCommit;
                            batchBytes += nextBytes;
                            estimatedFrameBytes += nextEstimated;
                            ackRequested |= next.AckRequested;
                        }

                        if (outOfOrder)
                        {
                            deltaBatch.Sort(CommitOrderComparison);
                        }

                        var push = new DeltaPushMessage(item.DatabaseId, localNodeId, deltaBatch, ackRequested);
                        await SendPushAsync(nodeId, item.DatabaseId, channel, QuicMessageType.DeltaPush, push, cancellationToken);
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

                        var ack = new DeltaAckMessage(item.DatabaseId, localNodeId, merged);
                        await SendPushAsync(nodeId, item.DatabaseId, channel, QuicMessageType.DeltaAck, ack, cancellationToken);
                        sent = true;
                        continue;
                    }

                    await SendControlAsync(nodeId, channel, item, cancellationToken);
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

    private ValueTask SendControlAsync(string nodeId, string channel, in OutboundItem item, CancellationToken cancellationToken) => item.Kind switch
    {
        OutboundKind.StrongCommit when item.StrongCommit is { } commit => SendPushAsync(nodeId, item.DatabaseId, channel, QuicMessageType.StrongCommit, commit, cancellationToken),
        OutboundKind.StrongRelease when item.StrongRelease is { } release => SendPushAsync(nodeId, item.DatabaseId, channel, QuicMessageType.StrongWound, release, cancellationToken),
        _ => throw new UnreachableException(),
    };

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder))]
    private async ValueTask SendPushAsync<TMessage>(string nodeId, Ulid databaseId, string channel, ushort messageType, TMessage message, CancellationToken cancellationToken)
        where TMessage : IBinaryMessage<TMessage>
    {
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

    private readonly record struct InboundItem(string SenderNodeId, IReadOnlyList<CommitMetadata> Commits, bool AckRequested);

    private sealed class InboundMailbox
    {
        public Lock Gate { get; } = new();

        public Queue<InboundItem> Queue { get; } = new();

        public Queue<TaskCompletionSource>? Waiters { get; set; }

        public List<(long Target, TaskCompletionSource Signal)>? FlushWaiters { get; set; }

        public long EnqueuedCount { get; set; }

        public long DequeuedCount { get; set; }

        public long AppliedCount { get; set; }

        public bool Draining { get; set; }

        public bool Removed { get; set; }
    }

    private sealed record PeerOutbox(Channel<OutboundItem> Queue, Task Pump);
}

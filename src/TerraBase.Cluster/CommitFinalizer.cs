using Microsoft.Extensions.Logging;

namespace TerraBase.Cluster;

public sealed class CommitFinalizer(InRegionRaftRouter raft, DeltaReplicator replicator, ClusterDatabases databases, ChangefeedPublisher changefeed, StrongLockTable locks, ILogger<CommitFinalizer> logger)
{
    public async ValueTask<InRegionReplicationOutcome> FinalizeFastAsync(Ulid databaseId, CommitMetadata commit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(commit);
        if (commit.Sequence == 0)
        {
            return new InRegionReplicated();
        }

        databases.Observe(databaseId, commit.Bookmark);
        var durable = await raft.ReplicateAsync(databaseId, commit, cancellationToken);
        replicator.PublishFast(databaseId, commit, ackRequested: false);
        await changefeed.PublishAsync(databaseId, commit, cancellationToken);
        return durable;
    }

    public async ValueTask FinalizeStrongAsync(Ulid databaseId, CommitMetadata commit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(commit);
        _ = locks.Release(databaseId, commit.TxId);
        if (commit.Sequence == 0)
        {
            return;
        }

        databases.Observe(databaseId, commit.Bookmark);
        replicator.BroadcastStrongCommit(databaseId, commit);
        var durable = await raft.ReplicateAsync(databaseId, commit, CancellationToken.None);
        var failure = durable switch
        {
            InRegionReplicated => null,
            ClusterUnavailable unavailable => unavailable.Message,
            ClusterTimeout timeout => timeout.Message,
        };
        if (failure is not null)
        {
            ClusterLog.InRegionQuorumUnconfirmed(logger, databaseId, commit.TxId, failure);
        }

        await changefeed.PublishAsync(databaseId, commit, cancellationToken);
    }

    public void Abort(Ulid databaseId, Ulid txId, HlcTimestamp priority, IReadOnlyList<string> remoteParticipants)
    {
        ArgumentNullException.ThrowIfNull(remoteParticipants);
        _ = locks.Release(databaseId, txId);
        replicator.BroadcastStrongRelease(databaseId, txId, txId, priority, remoteParticipants);
    }
}

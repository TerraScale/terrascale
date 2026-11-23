using Microsoft.Extensions.Logging;

namespace TerraBase.Cluster;

public sealed class CommitFinalizer(InRegionRaftRouter raft, DeltaReplicator replicator, ClusterDatabases databases, ChangefeedPublisher changefeed, StrongLockTable locks, ILogger<CommitFinalizer> logger)
{
    public async ValueTask<Result<Done, Unavailable, RequestTimeout>> FinalizeFastAsync(Ulid databaseId, CommitMetadata commit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(commit);
        if (commit.Sequence == 0)
        {
            return new Done();
        }

        databases.Observe(databaseId, commit.Bookmark);
        var durable = await raft.ReplicateAsync(databaseId, commit, cancellationToken).ConfigureAwait(false);
        replicator.PublishFast(databaseId, commit, ackRequested: false);
        await changefeed.PublishAsync(databaseId, commit, cancellationToken).ConfigureAwait(false);
        return durable;
    }

    public async ValueTask FinalizeStrongAsync(Ulid databaseId, CommitMetadata commit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(commit);
        databases.Observe(databaseId, commit.Bookmark);
        _ = locks.Release(databaseId, commit.TxId);
        replicator.BroadcastStrongCommit(databaseId, commit);
        var durable = await raft.ReplicateAsync(databaseId, commit, CancellationToken.None).ConfigureAwait(false);
        var failure = durable switch
        {
            Done => null,
            Unavailable unavailable => unavailable.Message,
            RequestTimeout timeout => timeout.Message,
        };
        if (failure is not null)
        {
            ClusterLog.InRegionQuorumUnconfirmed(logger, databaseId, commit.TxId, failure);
        }

        await changefeed.PublishAsync(databaseId, commit, cancellationToken).ConfigureAwait(false);
    }

    public void Abort(Ulid databaseId, Ulid txId, HlcTimestamp priority, IReadOnlyList<string> remoteParticipants)
    {
        ArgumentNullException.ThrowIfNull(remoteParticipants);
        _ = locks.Release(databaseId, txId);
        replicator.BroadcastStrongRelease(databaseId, txId, txId, priority, remoteParticipants);
    }
}

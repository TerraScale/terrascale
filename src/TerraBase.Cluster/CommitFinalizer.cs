using Microsoft.Extensions.Logging;

namespace TerraBase.Cluster;

public sealed class CommitFinalizer(InRegionRaftRouter raft, DeltaReplicator replicator, ClusterDatabases databases, ChangefeedPublisher changefeed, StrongLockTable locks, ILogger<CommitFinalizer> logger)
{
    private static readonly InRegionReplicated Replicated = new();

    public ValueTask<InRegionReplicationOutcome> FinalizeFastAsync(Ulid databaseId, CommitMetadata commit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(commit);
        if (commit.Sequence == 0)
        {
            return new(Replicated);
        }

        databases.Observe(databaseId, commit.Bookmark);
        if (!raft.IsReplicated && !changefeed.IsEnabled)
        {
            replicator.PublishFast(databaseId, commit, ackRequested: false);
            return new(Replicated);
        }

        return FinalizeFastSlowAsync(databaseId, commit, cancellationToken);
    }

    public ValueTask FinalizeStrongAsync(Ulid databaseId, CommitMetadata commit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(commit);
        _ = locks.Release(databaseId, commit.TxId);
        if (commit.Sequence == 0)
        {
            return ValueTask.CompletedTask;
        }

        databases.Observe(databaseId, commit.Bookmark);
        replicator.BroadcastStrongCommit(databaseId, commit);
        if (!raft.IsReplicated && !changefeed.IsEnabled)
        {
            return ValueTask.CompletedTask;
        }

        return FinalizeStrongSlowAsync(databaseId, commit, cancellationToken);
    }

    public void Abort(Ulid databaseId, Ulid txId, HlcTimestamp priority, IReadOnlyList<string> remoteParticipants)
    {
        ArgumentNullException.ThrowIfNull(remoteParticipants);
        _ = locks.Release(databaseId, txId);
        replicator.BroadcastStrongRelease(databaseId, txId, txId, priority, remoteParticipants);
    }

    private async ValueTask<InRegionReplicationOutcome> FinalizeFastSlowAsync(Ulid databaseId, CommitMetadata commit, CancellationToken cancellationToken)
    {
        var durable = await raft.ReplicateAsync(databaseId, commit, cancellationToken);
        replicator.PublishFast(databaseId, commit, ackRequested: false);
        if (changefeed.IsEnabled)
        {
            await changefeed.PublishAsync(databaseId, commit, cancellationToken);
        }

        return durable;
    }

    private async ValueTask FinalizeStrongSlowAsync(Ulid databaseId, CommitMetadata commit, CancellationToken cancellationToken)
    {
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

        if (changefeed.IsEnabled)
        {
            await changefeed.PublishAsync(databaseId, commit, cancellationToken);
        }
    }
}

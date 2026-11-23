using System.Diagnostics;
using System.Globalization;
using System.Net;
using DotNext.Net.Cluster.Consensus.Raft;
using DotNext.Net.Cluster.Consensus.Raft.StateMachine;
using Microsoft.Extensions.Logging;
using TerraScale.Contracts.Quic;
using TerraScale.QuicRpc;

namespace TerraBase.Cluster;

public sealed class InRegionRaftRouter(ClusterOptions options, QuicRpcClient client, DeltaReplicator replicator, ILoggerFactory loggerFactory) : IAsyncDisposable
{
    private const long SnapshotThreshold = 4096;

    private readonly ILogger _logger = loggerFactory.CreateLogger<InRegionRaftRouter>();
    private RaftShard[] _shards = [];
    private WriteAheadLog[] _logs = [];

    public bool IsReplicated => options.RegionPeers.Count > 0;

    public int ShardCount => options.RaftShardCount;

    public ushort ShardOf(Ulid databaseId) => RaftIdentity.ShardOf(databaseId, options.RaftShardCount);

    public string? LeaderOf(Ulid databaseId) =>
        !IsReplicated ? options.NodeId : Volatile.Read(ref _shards) is { Length: > 0 } shards ? shards[ShardOf(databaseId)].Leader?.NodeId : null;

    public void Map(QuicRpcHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        _ = host.MapUnary<RaftVoteMessage, RaftVoteResponse>(QuicMessageType.RaftRequestVote, HandleVoteAsync);
        _ = host.MapUnary<RaftAppendMessage, RaftAppendResponse>(QuicMessageType.RaftAppendEntries, HandleAppendAsync);
        _ = host.MapUnary<RaftSnapshotMessage, RaftAppendResponse>(QuicMessageType.RaftInstallSnapshot, HandleSnapshotAsync);
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!IsReplicated)
        {
            ClusterLog.RaftSingleNode(_logger, options.Region);
            return;
        }

        var configuration = new RaftMemberConfiguration(options);
        var shards = new RaftShard[options.RaftShardCount];
        var logs = new WriteAheadLog[options.RaftShardCount];
        var localId = RaftIdentity.For(options.NodeId);
        for (var index = 0; index < shards.Length; index++)
        {
            var shard = (ushort)index;
            var context = new RaftShardContext(shard, localId, client, options.RaftRequestTimeout);
            var location = Path.Combine(options.DataDirectory, "raft", string.Concat("shard-", shard.ToString("D3", CultureInfo.InvariantCulture)));
            _ = Directory.CreateDirectory(location);
            var machine = new RaftShardStateMachine(IStateMachine.CreateNoOp(SnapshotThreshold), ApplyAsync);
            logs[index] = new WriteAheadLog(new WriteAheadLog.Options { Location = location }, machine);
            shards[index] = new RaftShard(configuration, context, loggerFactory.CreateLogger<RaftShard>()) { AuditTrail = logs[index] };
            List<QuicRaftMember> members = [new QuicRaftMember(context, options.NodeId, options.ListenEndPoint, isRemote: false)];
            members.AddRange(options.RegionPeers.Select(peer => new QuicRaftMember(context, peer.NodeId, new DnsEndPoint(peer.Host, peer.QuicPort), isRemote: true)));
            await shards[index].StartShardAsync(members, cancellationToken).ConfigureAwait(false);
        }

        Volatile.Write(ref _logs, logs);
        Volatile.Write(ref _shards, shards);
        ClusterLog.RaftStarted(_logger, options.Region, shards.Length, options.RegionPeers.Count + 1);
    }

    public async ValueTask<Result<Done, Unavailable, RequestTimeout>> ReplicateAsync(Ulid databaseId, CommitMetadata commit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(commit);
        if (!IsReplicated || commit.Sequence == 0)
        {
            return new Done();
        }

        if (Volatile.Read(ref _shards) is not { Length: > 0 } shards)
        {
            return new Unavailable("in-region raft is not started", "raft_not_ready");
        }

        var shard = shards[ShardOf(databaseId)];
        var payload = new ReplicatedCommit(databaseId, commit).Encode();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(options.RaftCommitTimeout);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var leader = await RaftBoundary.LeaderAsync(shard, options.RaftCommitTimeout, cancellationToken).ConfigureAwait(false);
            if (leader is null)
            {
                return new RequestTimeout("in-region raft leader election did not finish in time");
            }

            var outcome = leader.IsRemote
                ? await ForwardAsync(leader.NodeId, shard.Context.Shard, payload, deadline.Token).ConfigureAwait(false)
                : await RaftBoundary.ReplicateAsync(shard, payload, deadline.Token, cancellationToken).ConfigureAwait(false);
            if (outcome == RaftProposeStatus.NotLeader)
            {
                continue;
            }

            return outcome switch
            {
                RaftProposeStatus.Committed => new Done(),
                RaftProposeStatus.Failed when cancellationToken.IsCancellationRequested || deadline.IsCancellationRequested => new RequestTimeout("in-region raft quorum was not reached in time"),
                RaftProposeStatus.Failed => new Unavailable("in-region raft quorum was not reached", "raft_quorum"),
                RaftProposeStatus.NotLeader => new Unavailable("in-region raft leadership moved", "raft_leader_moving"),
                _ => throw new UnreachableException(),
            };
        }

        return new Unavailable("in-region raft leadership kept moving", "raft_leader_moving");
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var shard in Interlocked.Exchange(ref _shards, []))
        {
            await RaftBoundary.StopAsync(shard).ConfigureAwait(false);
            foreach (var member in shard.Members)
            {
                member.Dispose();
            }

            await shard.DisposeAsync().ConfigureAwait(false);
        }

        foreach (var log in Interlocked.Exchange(ref _logs, []))
        {
            await log.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async ValueTask ApplyAsync(ReplicatedCommit replicated, CancellationToken cancellationToken)
    {
        if (string.Equals(replicated.Commit.OriginRegion, options.StorageOrigin, StringComparison.Ordinal))
        {
            return;
        }

        var applied = await replicator.ApplyAsync(replicated.DatabaseId, [replicated.Commit], cancellationToken).ConfigureAwait(false);
        var failure = applied switch
        {
            RemoteApplyOutcome => null,
            NotFound notFound => notFound.Message,
            SchemaMismatch mismatch => string.Concat("schema epoch ", mismatch.ActualEpoch.ToString(CultureInfo.InvariantCulture)),
            StorageCorrupted corrupted => corrupted.Message,
            StorageBusy busy => busy.Message,
            Unavailable unavailable => unavailable.Message,
        };
        if (failure is not null)
        {
            ClusterLog.RaftApplyDeferred(_logger, replicated.DatabaseId, failure);
        }
    }

    private async ValueTask<RaftProposeStatus> ForwardAsync(string leaderNodeId, ushort shard, byte[] payload, CancellationToken cancellationToken)
    {
        var message = new RaftAppendMessage(shard, RaftAppendKind.Propose, default, 0, 0, 0, 0, 0, [new RaftWireEntry(0, null, false, payload)]);
        var response = await client.CallAsync<RaftAppendMessage, RaftAppendResponse>(leaderNodeId, QuicMessageType.RaftAppendEntries, message, options.RaftCommitTimeout, cancellationToken).ConfigureAwait(false);
        return response switch
        {
            RaftAppendResponse received => received.Status,
            Unavailable or RequestTimeout or QuicRemoteError => RaftProposeStatus.Failed,
        };
    }

    private RaftShard? Shard(ushort index) =>
        Volatile.Read(ref _shards) is { } shards && index < shards.Length ? shards[index] : null;

    private async ValueTask<Result<RaftVoteResponse, QuicRemoteError>> HandleVoteAsync(QuicCallContext context, RaftVoteMessage message, CancellationToken cancellationToken)
    {
        if (Shard(message.Shard) is not { } shard)
        {
            return NotReady();
        }

        if (!Enum.IsDefined(message.Kind))
        {
            return new QuicRemoteError(QuicRemoteError.MalformedRequest, "unknown raft vote kind");
        }

        return await shard.HandleVoteAsync(message, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<Result<RaftAppendResponse, QuicRemoteError>> HandleAppendAsync(QuicCallContext context, RaftAppendMessage message, CancellationToken cancellationToken)
    {
        if (Shard(message.Shard) is not { } shard)
        {
            return NotReady();
        }

        if (message.Kind == RaftAppendKind.Propose)
        {
            if (message.Entries.Count != 1)
            {
                return new QuicRemoteError(QuicRemoteError.MalformedRequest, "a raft proposal carries exactly one entry");
            }

            var status = shard.Leader is { IsRemote: false }
                ? await RaftBoundary.ReplicateAsync(shard, message.Entries[0].Payload.ToArray(), cancellationToken, cancellationToken).ConfigureAwait(false)
                : RaftProposeStatus.NotLeader;
            return new RaftAppendResponse(shard.CurrentTerm, 0, 0, status);
        }

        return await shard.HandleAppendAsync(message, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<Result<RaftAppendResponse, QuicRemoteError>> HandleSnapshotAsync(QuicCallContext context, RaftSnapshotMessage message, CancellationToken cancellationToken)
    {
        if (Shard(message.Shard) is not { } shard)
        {
            return NotReady();
        }

        return await shard.HandleSnapshotAsync(message, cancellationToken).ConfigureAwait(false);
    }

    private static QuicRemoteError NotReady() => new("raft_not_ready", "in-region raft shard is not started on this node");
}

internal static class RaftBoundary
{
    public static async ValueTask<QuicRaftMember?> LeaderAsync(RaftShard shard, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (shard.Leader is { } leader)
        {
            return leader;
        }

        try
        {
            return await shard.WaitForLeaderAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return null;
        }
    }

    public static async ValueTask<RaftProposeStatus> ReplicateAsync(RaftShard shard, byte[] payload, CancellationToken deadline, CancellationToken cancellationToken)
    {
        try
        {
            await shard.ReplicateAsync(new RaftPayloadEntry(shard.CurrentTerm, null, false, false, payload), deadline).ConfigureAwait(false);
            return RaftProposeStatus.Committed;
        }
        catch (NotLeaderException)
        {
            return RaftProposeStatus.NotLeader;
        }
        catch (QuorumUnreachableException)
        {
            return RaftProposeStatus.Failed;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return RaftProposeStatus.Failed;
        }
    }

    public static async ValueTask StopAsync(RaftShard shard)
    {
        try
        {
            await shard.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            return;
        }
    }
}

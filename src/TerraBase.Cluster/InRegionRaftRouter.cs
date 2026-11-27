using System.Diagnostics;
using System.Globalization;
using System.Net;
using DotNext.Net.Cluster.Consensus.Raft;
using DotNext.Net.Cluster.Consensus.Raft.StateMachine;
using Microsoft.Extensions.Logging;
using TerraScale.Contracts.Quic;
using TerraScale.QuicRpc;

namespace TerraBase.Cluster;

public sealed record InRegionReplicated;

public readonly union InRegionReplicationOutcome(InRegionReplicated, ClusterUnavailable, ClusterTimeout);

public sealed class InRegionRaftRouter(ClusterOptions options, ClusterTopology topology, QuicRpcClient client, DeltaReplicator replicator, ILoggerFactory loggerFactory) : IAsyncDisposable
{
    private const long SnapshotThreshold = 256;

    private const UnixFileMode PrivateDirectory = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    private const UnixFileMode PrivateFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private readonly ILogger _logger = loggerFactory.CreateLogger<InRegionRaftRouter>();
    private RaftShard[] _shards = [];
    private WriteAheadLog[] _logs = [];

    public bool IsReplicated => options.RegionPeers.Count >= 2;

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
        var root = Path.Combine(options.DataDirectory, "raft");
        Restrict(root);
        for (var index = 0; index < shards.Length; index++)
        {
            var shard = (ushort)index;
            var context = new RaftShardContext(shard, localId, client, options.RaftRequestTimeout);
            var location = Path.Combine(root, string.Concat("shard-", shard.ToString("D3", CultureInfo.InvariantCulture)));
            Restrict(location);
            var machine = new RaftShardStateMachine(IStateMachine.CreateNoOp(SnapshotThreshold), ApplyAsync);
            logs[index] = new WriteAheadLog(new WriteAheadLog.Options { Location = location }, machine);
            Restrict(location);
            shards[index] = new RaftShard(configuration, context, loggerFactory.CreateLogger<RaftShard>()) { AuditTrail = logs[index] };
            List<QuicRaftMember> members = [new QuicRaftMember(context, options.NodeId, options.ListenEndPoint, isRemote: false)];
            members.AddRange(options.RegionPeers.Select(peer => new QuicRaftMember(context, peer.NodeId, new DnsEndPoint(peer.Host, peer.QuicPort), isRemote: true)));
            await shards[index].StartShardAsync(members, cancellationToken);
        }

        Volatile.Write(ref _logs, logs);
        Volatile.Write(ref _shards, shards);
        ClusterLog.RaftStarted(_logger, options.Region, shards.Length, options.RegionPeers.Count + 1);
    }

    private static void Restrict(string location)
    {
        if (OperatingSystem.IsWindows())
        {
            _ = Directory.CreateDirectory(location);
            return;
        }

        _ = Directory.CreateDirectory(location, PrivateDirectory);
        File.SetUnixFileMode(location, PrivateDirectory);
        foreach (var directory in Directory.EnumerateDirectories(location, "*", SearchOption.AllDirectories))
        {
            File.SetUnixFileMode(directory, PrivateDirectory);
        }

        foreach (var file in Directory.EnumerateFiles(location, "*", SearchOption.AllDirectories))
        {
            File.SetUnixFileMode(file, PrivateFile);
        }
    }

    public async ValueTask<InRegionReplicationOutcome> ReplicateAsync(Ulid databaseId, CommitMetadata commit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(commit);
        if (!IsReplicated || commit.Sequence == 0)
        {
            return new InRegionReplicated();
        }

        if (Volatile.Read(ref _shards) is not { Length: > 0 } shards)
        {
            return new ClusterUnavailable("in-region raft is not started", "raft_not_ready");
        }

        var shard = shards[ShardOf(databaseId)];
        var payload = new ReplicatedCommit(databaseId, commit).Encode();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(options.RaftCommitTimeout);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var leader = await RaftBoundary.LeaderAsync(shard, options.RaftCommitTimeout, cancellationToken);
            if (leader is null)
            {
                return new ClusterTimeout("in-region raft leader election did not finish in time");
            }

            var outcome = leader.IsRemote
                ? await ForwardAsync(leader.NodeId, shard.Context.Shard, payload, deadline.Token, cancellationToken)
                : await RaftBoundary.ReplicateAsync(shard, payload, deadline.Token, cancellationToken);
            if (outcome == RaftProposeStatus.NotLeader)
            {
                continue;
            }

            return outcome switch
            {
                RaftProposeStatus.Committed => new InRegionReplicated(),
                RaftProposeStatus.Failed when cancellationToken.IsCancellationRequested || deadline.IsCancellationRequested => new ClusterTimeout("in-region raft quorum was not reached in time"),
                RaftProposeStatus.Failed => new ClusterUnavailable("in-region raft quorum was not reached", "raft_quorum"),
                RaftProposeStatus.NotLeader => new ClusterUnavailable("in-region raft leadership moved", "raft_leader_moving"),
                _ => throw new UnreachableException(),
            };
        }

        return new ClusterUnavailable("in-region raft leadership kept moving", "raft_leader_moving");
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var shard in Interlocked.Exchange(ref _shards, []))
        {
            await RaftBoundary.StopAsync(shard);
            foreach (var member in shard.Members)
            {
                member.Dispose();
            }

            await shard.DisposeAsync();
        }

        foreach (var log in Interlocked.Exchange(ref _logs, []))
        {
            await log.DisposeAsync();
        }
    }

    private async ValueTask ApplyAsync(ReplicatedCommit replicated, CancellationToken cancellationToken)
    {
        if (string.Equals(replicated.Commit.OriginRegion, options.StorageOrigin, StringComparison.Ordinal))
        {
            return;
        }

        var applied = await replicator.ApplyAsync(replicated.DatabaseId, [replicated.Commit], cancellationToken);
        var failure = applied switch
        {
            RemoteApplyOutcome => null,
            DatabaseNotFound notFound => notFound.Message,
            SchemaMismatch mismatch => string.Concat("schema epoch ", mismatch.ActualEpoch.ToString(CultureInfo.InvariantCulture)),
            StorageCorrupted corrupted => corrupted.Message,
            StorageBusy busy => busy.Message,
            ClusterUnavailable unavailable => unavailable.Message,
        };
        if (failure is not null)
        {
            ClusterLog.RaftApplyDeferred(_logger, replicated.DatabaseId, failure);
        }
    }

    private async ValueTask<RaftProposeStatus> ForwardAsync(string leaderNodeId, ushort shard, byte[] payload, CancellationToken deadline, CancellationToken cancellationToken)
    {
        var message = new RaftAppendMessage(shard, RaftAppendKind.Propose, default, 0, 0, 0, 0, 0, [new RaftWireEntry(0, null, false, payload)]);
        try
        {
            var response = await client.CallAsync<RaftAppendMessage, RaftAppendResponse>(leaderNodeId, QuicMessageType.RaftAppendEntries, message, options.RaftCommitTimeout, deadline);
            return response switch
            {
                RaftAppendResponse received => received.Status,
                QuicUnavailable or QuicTimeout or QuicRemoteError => RaftProposeStatus.Failed,
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return RaftProposeStatus.Failed;
        }
    }

    private RaftShard? Shard(ushort index) =>
        Volatile.Read(ref _shards) is { } shards && index < shards.Length ? shards[index] : null;

    private async ValueTask<QuicHandlerOutcome<RaftVoteResponse>> HandleVoteAsync(QuicCallContext context, RaftVoteMessage message, CancellationToken cancellationToken)
    {
        if (!topology.IsRegionPeer(context) || message.Sender != RaftIdentity.For(context.RemoteNodeId))
        {
            return ClusterTopology.PeerForbidden();
        }

        if (Shard(message.Shard) is not { } shard)
        {
            return NotReady();
        }

        if (!Enum.IsDefined(message.Kind))
        {
            return new QuicRemoteError(QuicRemoteError.MalformedRequest, "unknown raft vote kind");
        }

        return await shard.HandleVoteAsync(message, cancellationToken);
    }

    private async ValueTask<QuicHandlerOutcome<RaftAppendResponse>> HandleAppendAsync(QuicCallContext context, RaftAppendMessage message, CancellationToken cancellationToken)
    {
        if (!topology.IsRegionPeer(context))
        {
            return ClusterTopology.PeerForbidden();
        }

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

            if (ReplicatedCommit.Decode(message.Entries[0].Payload.Span) is not { } proposed || ShardOf(proposed.DatabaseId) != message.Shard)
            {
                return new QuicRemoteError(QuicRemoteError.MalformedRequest, "raft proposal is not a commit for this shard");
            }

            if (!topology.IsPeerOrigin(context, proposed.Commit.OriginRegion))
            {
                return ClusterTopology.PeerForbidden();
            }

            var status = shard.Leader is { IsRemote: false }
                ? await RaftBoundary.ReplicateAsync(shard, message.Entries[0].Payload.ToArray(), cancellationToken, cancellationToken)
                : RaftProposeStatus.NotLeader;
            return new RaftAppendResponse(shard.CurrentTerm, 0, 0, status);
        }

        if (message.Kind != RaftAppendKind.Append)
        {
            return new QuicRemoteError(QuicRemoteError.MalformedRequest, "unknown raft append kind");
        }

        if (message.Sender != RaftIdentity.For(context.RemoteNodeId) || message.Entries.Any(entry => !IsRegionEntry(message.Shard, entry)))
        {
            return ClusterTopology.PeerForbidden();
        }

        return await shard.HandleAppendAsync(message, cancellationToken);
    }

    private async ValueTask<QuicHandlerOutcome<RaftAppendResponse>> HandleSnapshotAsync(QuicCallContext context, RaftSnapshotMessage message, CancellationToken cancellationToken)
    {
        if (!topology.IsRegionPeer(context) || message.Sender != RaftIdentity.For(context.RemoteNodeId))
        {
            return ClusterTopology.PeerForbidden();
        }

        if (Shard(message.Shard) is not { } shard)
        {
            return NotReady();
        }

        return await shard.HandleSnapshotAsync(message, cancellationToken);
    }

    private bool IsRegionEntry(ushort shard, RaftWireEntry entry) =>
        entry.IsConfiguration || entry.Payload.IsEmpty || (ReplicatedCommit.Decode(entry.Payload.Span) is { } replicated
            && ShardOf(replicated.DatabaseId) == shard && topology.IsRegionOrigin(options.Region, replicated.Commit.OriginRegion));

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
            return await shard.WaitForLeaderAsync(timeout, cancellationToken);
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
            await shard.ReplicateAsync(new RaftPayloadEntry(shard.CurrentTerm, null, false, false, payload), deadline);
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
            await shard.StopAsync(CancellationToken.None);
        }
        catch (ObjectDisposedException)
        {
            return;
        }
    }
}

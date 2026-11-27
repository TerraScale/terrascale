using System.Collections.Frozen;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using DotNext.Net.Cluster;
using Microsoft.Extensions.Logging;
using TerraScale.Contracts.Quic;
using TerraScale.QuicRpc;

namespace TerraBase.Cluster;

public sealed record InRegionReplicated;

public readonly union InRegionReplicationOutcome(InRegionReplicated, ClusterUnavailable, ClusterTimeout);

public sealed class InRegionRaftRouter : IAsyncDisposable
{
    private const long SnapshotThreshold = 256;

    private const UnixFileMode PrivateDirectory = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    private const UnixFileMode PrivateFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private static readonly InRegionReplicated Replicated = new();

    private readonly ClusterOptions _options;
    private readonly ClusterTopology _topology;
    private readonly QuicRpcClient _client;
    private readonly DeltaReplicator _replicator;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _logger;
    private readonly FrozenDictionary<string, ClusterMemberId> _memberIds;
    private readonly byte[] _localOriginUtf8;
    private readonly bool _isReplicated;
    private RaftShard[] _shards = [];

    public InRegionRaftRouter(ClusterOptions options, ClusterTopology topology, QuicRpcClient client, DeltaReplicator replicator, ILoggerFactory loggerFactory)
    {
        _options = options;
        _topology = topology;
        _client = client;
        _replicator = replicator;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<InRegionRaftRouter>();
        _isReplicated = topology.RegionPeers.Count >= 2;
        _localOriginUtf8 = Encoding.UTF8.GetBytes(options.StorageOrigin);
        var ids = new Dictionary<string, ClusterMemberId>(topology.RegionPeers.Count + 1, StringComparer.Ordinal)
        {
            [options.NodeId] = RaftIdentity.For(options.NodeId),
        };
        for (var index = 0; index < topology.RegionPeers.Count; index++)
        {
            var peerId = topology.RegionPeers[index].NodeId;
            ids[peerId] = RaftIdentity.For(peerId);
        }

        _memberIds = ids.ToFrozenDictionary(StringComparer.Ordinal);
    }

    public bool IsReplicated => _isReplicated;

    public int ShardCount => _options.RaftShardCount;

    public ushort ShardOf(Ulid databaseId) => RaftIdentity.ShardOf(databaseId, _options.RaftShardCount);

    public string? LeaderOf(Ulid databaseId) =>
        !_isReplicated ? _options.NodeId : Volatile.Read(ref _shards) is { Length: > 0 } shards ? shards[ShardOf(databaseId)].Leader?.NodeId : null;

    public void Map(QuicRpcHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        _ = host.MapUnary<RaftVoteMessage, RaftVoteResponse>(QuicMessageType.RaftRequestVote, HandleVoteAsync);
        _ = host.MapUnary<RaftAppendMessage, RaftAppendResponse>(QuicMessageType.RaftAppendEntries, HandleAppendAsync);
        _ = host.MapUnary<RaftSnapshotMessage, RaftAppendResponse>(QuicMessageType.RaftInstallSnapshot, HandleSnapshotAsync);
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_isReplicated)
        {
            ClusterLog.RaftSingleNode(_logger, _options.Region);
            return Task.CompletedTask;
        }

        var shards = new RaftShard[_options.RaftShardCount];
        var localId = _memberIds[_options.NodeId];
        var root = Path.Combine(_options.DataDirectory, "raft");
        Restrict(root);
        var regionPeers = _topology.RegionPeers;
        for (var index = 0; index < shards.Length; index++)
        {
            var shardIndex = (ushort)index;
            var location = Path.Combine(root, string.Concat("shard-", shardIndex.ToString("D3", CultureInfo.InvariantCulture)));
            Restrict(location);
            var log = new RaftShardLog(location, SnapshotThreshold, ApplyRawAsync);
            var local = new QuicRaftMember(shardIndex, localId, _options.NodeId, localId, isRemote: false, _client, _options.RaftRequestTimeout);
            var peers = new QuicRaftMember[regionPeers.Count];
            for (var peerIndex = 0; peerIndex < regionPeers.Count; peerIndex++)
            {
                var peer = regionPeers[peerIndex];
                peers[peerIndex] = new QuicRaftMember(shardIndex, localId, peer.NodeId, _memberIds[peer.NodeId], isRemote: true, _client, _options.RaftRequestTimeout);
            }

            shards[index] = new RaftShard(shardIndex, local, peers, log, _options, _loggerFactory.CreateLogger<RaftShard>());
            shards[index].Start();
        }

        Volatile.Write(ref _shards, shards);
        ClusterLog.RaftStarted(_logger, _options.Region, shards.Length, regionPeers.Count + 1);
        return Task.CompletedTask;
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

    public ValueTask<InRegionReplicationOutcome> ReplicateAsync(Ulid databaseId, CommitMetadata commit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(commit);
        if (!_isReplicated || commit.Sequence == 0)
        {
            return new(Replicated);
        }

        return ReplicateSlowAsync(databaseId, commit, cancellationToken);
    }

    private async ValueTask<InRegionReplicationOutcome> ReplicateSlowAsync(Ulid databaseId, CommitMetadata commit, CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _shards) is not { Length: > 0 } shards)
        {
            return new ClusterUnavailable("in-region raft is not started", "raft_not_ready");
        }

        var shard = shards[ShardOf(databaseId)];
        var payload = new ReplicatedCommit(databaseId, commit).Encode();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_options.RaftCommitTimeout);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var leader = await shard.WaitForLeaderAsync(_options.RaftCommitTimeout, cancellationToken);
            if (leader is null)
            {
                return new ClusterTimeout("in-region raft leader election did not finish in time");
            }

            var outcome = leader.IsRemote
                ? await ForwardAsync(leader.NodeId, shard.Shard, payload, deadline.Token, cancellationToken)
                : await shard.ReplicateAsync(payload, deadline.Token, cancellationToken);
            if (outcome == RaftProposeStatus.NotLeader)
            {
                continue;
            }

            return outcome switch
            {
                RaftProposeStatus.Committed => Replicated,
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
            await shard.DisposeAsync();
        }
    }

    private ValueTask ApplyRawAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        var span = payload.Span;
        if (ReplicatedCommit.TryPeekHeader(span, out _, out var originUtf8) && originUtf8.SequenceEqual(_localOriginUtf8))
        {
            return ValueTask.CompletedTask;
        }

        if (ReplicatedCommit.Decode(span) is not { } replicated)
        {
            return ValueTask.CompletedTask;
        }

        if (string.Equals(replicated.Commit.OriginRegion, _options.StorageOrigin, StringComparison.Ordinal))
        {
            return ValueTask.CompletedTask;
        }

        return ApplyRemoteAsync(replicated, cancellationToken);
    }

    private async ValueTask ApplyRemoteAsync(ReplicatedCommit replicated, CancellationToken cancellationToken)
    {
        var applied = await _replicator.ApplyAsync(replicated.DatabaseId, [replicated.Commit], cancellationToken);
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

    private async ValueTask<RaftProposeStatus> ForwardAsync(string leaderNodeId, ushort shard, ReadOnlyMemory<byte> payload, CancellationToken deadline, CancellationToken cancellationToken)
    {
        var message = new RaftAppendMessage(shard, RaftAppendKind.Propose, default, 0, 0, 0, 0, 0, [new RaftWireEntry(0, null, false, payload)]);
        try
        {
            var response = await _client.CallAsync<RaftAppendMessage, RaftAppendResponse>(leaderNodeId, QuicMessageType.RaftAppendEntries, message, _options.RaftCommitTimeout, deadline);
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

    private bool MatchesMemberId(string nodeId, ClusterMemberId sender) =>
        _memberIds.TryGetValue(nodeId, out var expected) && sender == expected;

    private async ValueTask<QuicHandlerOutcome<RaftVoteResponse>> HandleVoteAsync(QuicCallContext context, RaftVoteMessage message, CancellationToken cancellationToken)
    {
        if (!_topology.IsRegionPeer(context) || !MatchesMemberId(context.RemoteNodeId, message.Sender))
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
        if (!_topology.IsRegionPeer(context))
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

            var payload = message.Entries[0].Payload;
            if (!ReplicatedCommit.TryPeekHeader(payload.Span, out var databaseId, out var originUtf8) || ShardOf(databaseId) != message.Shard)
            {
                return new QuicRemoteError(QuicRemoteError.MalformedRequest, "raft proposal is not a commit for this shard");
            }

            var origin = ClusterCodec.DecodePooledString(originUtf8);
            if (!_topology.IsPeerOrigin(context, origin))
            {
                return ClusterTopology.PeerForbidden();
            }

            var status = shard.IsLocalLeader
                ? await shard.ReplicateAsync(payload, cancellationToken, cancellationToken)
                : RaftProposeStatus.NotLeader;
            return new RaftAppendResponse(shard.CurrentTerm, 0, 0, status);
        }

        if (message.Kind != RaftAppendKind.Append)
        {
            return new QuicRemoteError(QuicRemoteError.MalformedRequest, "unknown raft append kind");
        }

        if (!MatchesMemberId(context.RemoteNodeId, message.Sender))
        {
            return ClusterTopology.PeerForbidden();
        }

        var entries = message.Entries;
        for (var index = 0; index < entries.Count; index++)
        {
            if (!IsRegionEntry(message.Shard, entries[index]))
            {
                return ClusterTopology.PeerForbidden();
            }
        }

        return await shard.HandleAppendAsync(message, cancellationToken);
    }

    private async ValueTask<QuicHandlerOutcome<RaftAppendResponse>> HandleSnapshotAsync(QuicCallContext context, RaftSnapshotMessage message, CancellationToken cancellationToken)
    {
        if (!_topology.IsRegionPeer(context) || !MatchesMemberId(context.RemoteNodeId, message.Sender))
        {
            return ClusterTopology.PeerForbidden();
        }

        if (Shard(message.Shard) is not { } shard)
        {
            return NotReady();
        }

        return await shard.HandleSnapshotAsync(message, cancellationToken);
    }

    private bool IsRegionEntry(ushort shard, RaftWireEntry entry)
    {
        if (entry.IsConfiguration || entry.Payload.IsEmpty)
        {
            return true;
        }

        if (!ReplicatedCommit.TryPeekHeader(entry.Payload.Span, out var databaseId, out var originUtf8) || ShardOf(databaseId) != shard)
        {
            return false;
        }

        return _topology.IsRegionOrigin(_options.Region, ClusterCodec.DecodePooledString(originUtf8));
    }

    private static QuicRemoteError NotReady() => new("raft_not_ready", "in-region raft shard is not started on this node");
}

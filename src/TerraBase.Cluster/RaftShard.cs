using System.Buffers;
using System.Diagnostics;
using DotNext.Net.Cluster;
using DotNext.Net.Cluster.Consensus.Raft;
using DotNext.Net.Cluster.Consensus.Raft.StateMachine;
using Microsoft.Extensions.Logging;
using TerraScale.QuicRpc;

namespace TerraBase.Cluster;

internal sealed class RaftShardContext(ushort shard, ClusterMemberId localId, QuicRpcClient client, TimeSpan requestTimeout)
{
    public ushort Shard { get; } = shard;

    public ClusterMemberId LocalId { get; } = localId;

    public QuicRpcClient Client { get; } = client;

    public TimeSpan RequestTimeout { get; } = requestTimeout;

    public int StateVersion { get; set; }

    public RaftShard? Owner { get; set; }

    public ClusterMemberId? LeaderId => Owner?.Leader?.Id;
}

internal sealed class RaftMemberConfiguration(ClusterOptions options) : IClusterMemberConfiguration
{
    public double HeartbeatThreshold => 0.3;

    public ElectionTimeout ElectionTimeout { get; } = new()
    {
        LowerValue = (int)options.RaftElectionTimeoutLower.TotalMilliseconds,
        UpperValue = (int)options.RaftElectionTimeoutUpper.TotalMilliseconds,
    };

    public bool Standby => false;

    public bool IsLeaderLeaseEnabled => false;
}

internal sealed class RaftShard(RaftMemberConfiguration configuration, RaftShardContext context, ILogger logger) : RaftCluster<QuicRaftMember>(configuration)
{
    public RaftShardContext Context => context;

    public long CurrentTerm => AuditTrail.Term;

    protected override ILogger Logger => logger;

    public async Task StartShardAsync(IReadOnlyList<QuicRaftMember> members, CancellationToken cancellationToken)
    {
        context.StateVersion = AuditTrail.Version;
        context.Owner = this;
        var scope = await ChangeConfigurationAsync(cancellationToken).ConfigureAwait(false);
        await using (scope.ConfigureAwait(false))
        {
            foreach (var member in members)
            {
                scope.MarkAsAdded(member);
            }
        }

        await StartAsync(cancellationToken).ConfigureAwait(false);
        StartFollowing();
    }

    public ValueTask<RaftVoteResponse> HandleVoteAsync(RaftVoteMessage message, CancellationToken cancellationToken) => message.Kind switch
    {
        RaftVoteKind.Vote => VoteResponseAsync(message, cancellationToken),
        RaftVoteKind.PreVote => PreVoteResponseAsync(message, cancellationToken),
        RaftVoteKind.Resign => ResignResponseAsync(cancellationToken),
        RaftVoteKind.Synchronize => SynchronizeResponseAsync(message.LastLogIndex, cancellationToken),
        _ => throw new UnreachableException(),
    };

    public async ValueTask<RaftAppendResponse> HandleAppendAsync(RaftAppendMessage message, CancellationToken cancellationToken)
    {
        var result = await ReceiveAppendAsync(message, cancellationToken).ConfigureAwait(false);
        return new RaftAppendResponse(result.Term, (byte)result.Value.Result, result.Value.LastIndex, RaftProposeStatus.Committed);
    }

    public async ValueTask<RaftAppendResponse> HandleSnapshotAsync(RaftSnapshotMessage message, CancellationToken cancellationToken)
    {
        var result = await ReceiveSnapshotAsync(message, cancellationToken).ConfigureAwait(false);
        return new RaftAppendResponse(result.Term, (byte)result.Value, 0, RaftProposeStatus.Committed);
    }

    private async ValueTask<RaftVoteResponse> VoteResponseAsync(RaftVoteMessage message, CancellationToken cancellationToken)
    {
        var vote = await ReceiveVoteAsync(message, cancellationToken).ConfigureAwait(false);
        return new RaftVoteResponse(vote.Term, vote.Value ? 1 : 0, true);
    }

    private async ValueTask<RaftVoteResponse> PreVoteResponseAsync(RaftVoteMessage message, CancellationToken cancellationToken)
    {
        var preVote = await ReceivePreVoteAsync(message, cancellationToken).ConfigureAwait(false);
        return new RaftVoteResponse(preVote.Term, (long)preVote.Value, true);
    }

    private async ValueTask<RaftVoteResponse> ResignResponseAsync(CancellationToken cancellationToken) =>
        new(0, await ResignAsync(cancellationToken).ConfigureAwait(false) ? 1 : 0, true);

    private async ValueTask<RaftVoteResponse> SynchronizeResponseAsync(long commitIndex, CancellationToken cancellationToken)
    {
        var index = await SynchronizeAsync(commitIndex, cancellationToken).ConfigureAwait(false);
        return new RaftVoteResponse(0, index.GetValueOrDefault(), index.HasValue);
    }

    private ValueTask<Result<bool>> ReceiveVoteAsync(RaftVoteMessage message, CancellationToken cancellationToken) =>
        VoteAsync(message.Sender, message.Term, message.LastLogIndex, message.LastLogTerm, message.StateVersion, cancellationToken);

    private ValueTask<Result<PreVoteResult>> ReceivePreVoteAsync(RaftVoteMessage message, CancellationToken cancellationToken) =>
        PreVoteAsync(message.Sender, message.Term, message.LastLogIndex, message.LastLogTerm, message.StateVersion, cancellationToken);

    private async ValueTask<Result<ReplicationStatus>> ReceiveAppendAsync(RaftAppendMessage message, CancellationToken cancellationToken)
    {
        var producer = new RaftEntryProducer(message.Entries);
        await using (producer.ConfigureAwait(false))
        {
            return await AppendEntriesAsync(message.Sender, message.Term, producer, message.PrevLogIndex, message.PrevLogTerm, message.CommitIndex, message.StateVersion, cancellationToken).ConfigureAwait(false);
        }
    }

    private ValueTask<Result<HeartbeatResult>> ReceiveSnapshotAsync(RaftSnapshotMessage message, CancellationToken cancellationToken)
    {
        var snapshot = new RaftPayloadEntry(message.Snapshot.Term, message.Snapshot.CommandId, message.Snapshot.IsConfiguration, true, message.Snapshot.Payload);
        return InstallSnapshotAsync(message.Sender, message.Term, snapshot, message.SnapshotIndex, message.StateVersion, cancellationToken);
    }
}

internal sealed class RaftShardStateMachine(IStateMachine snapshots, Func<ReplicatedCommit, CancellationToken, ValueTask> apply) : IStateMachine
{
    public ISnapshot? Snapshot => snapshots.Snapshot;

    public int Version => snapshots.Version;

    public ValueTask ReclaimGarbageAsync(long watermark, CancellationToken token) => snapshots.ReclaimGarbageAsync(watermark, token);

    public async ValueTask<long> ApplyAsync(LogEntry entry, CancellationToken token)
    {
        if (!entry.IsConfiguration && !entry.IsSnapshot && ReplicatedCommit.Decode(Payload(entry)) is { } commit)
        {
            await apply(commit, token).ConfigureAwait(false);
        }

        return await snapshots.ApplyAsync(entry, token).ConfigureAwait(false);
    }

    private static ReadOnlySpan<byte> Payload(LogEntry entry)
    {
        if (entry.TryGetPayload(out var sequence))
        {
            return sequence.IsSingleSegment ? sequence.FirstSpan : sequence.ToArray();
        }

        var buffer = new ArrayBufferWriter<byte>();
        foreach (var segment in entry.GetPayload())
        {
            buffer.Write(segment.Span);
        }

        return buffer.WrittenSpan;
    }
}

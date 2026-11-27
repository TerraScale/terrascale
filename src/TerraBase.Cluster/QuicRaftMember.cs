using System.Collections.Frozen;
using System.Net;
using DotNext.IO;
using DotNext.Net.Cluster;
using DotNext.Net.Cluster.Consensus.Raft;
using TerraScale.Contracts.Quic;
using TerraScale.QuicRpc;

namespace TerraBase.Cluster;

internal sealed class QuicRaftMember(RaftShardContext context, string nodeId, ClusterMemberId memberId, EndPoint endPoint, bool isRemote) : IRaftClusterMember, IDisposable
{
    private readonly CancellationTokenSource _pending = new();
    private readonly IReadOnlyDictionary<string, string> _metadata = new Dictionary<string, string>(StringComparer.Ordinal) { ["node"] = nodeId }.ToFrozenDictionary(StringComparer.Ordinal);
    private IRaftClusterMember.ReplicationState _state;
    private int _status = (int)(isRemote ? ClusterMemberStatus.Unknown : ClusterMemberStatus.Available);

    public event Action<ClusterMemberStatusChangedEventArgs>? MemberStatusChanged;

    public string NodeId { get; } = nodeId;

    public ClusterMemberId Id { get; } = memberId;

    public EndPoint EndPoint { get; } = endPoint;

    public bool IsRemote { get; } = isRemote;

    public bool IsLeader => context.LeaderId is { } leader && leader == Id;

    public ClusterMemberStatus Status => (ClusterMemberStatus)Volatile.Read(ref _status);

    ref IRaftClusterMember.ReplicationState IRaftClusterMember.State => ref _state;

    public ValueTask<IReadOnlyDictionary<string, string>> GetMetadataAsync(bool refresh, CancellationToken token) =>
        ValueTask.FromResult(_metadata);

    public async Task<bool> ResignAsync(CancellationToken token) =>
        (await VoteCallAsync(RaftVoteKind.Resign, 0, 0, 0, token)).Value != 0;

    public async Task<Result<bool>> VoteAsync(long term, long lastLogIndex, long lastLogTerm, CancellationToken token)
    {
        if (!IsRemote)
        {
            return new Result<bool> { Term = term, Value = true };
        }

        var response = await VoteCallAsync(RaftVoteKind.Vote, term, lastLogIndex, lastLogTerm, token);
        return new Result<bool> { Term = response.Term, Value = response.Value != 0 };
    }

    public async Task<Result<PreVoteResult>> PreVoteAsync(long term, long lastLogIndex, long lastLogTerm, CancellationToken token)
    {
        if (!IsRemote)
        {
            return new Result<PreVoteResult> { Term = term, Value = PreVoteResult.Accepted };
        }

        var response = await VoteCallAsync(RaftVoteKind.PreVote, term, lastLogIndex, lastLogTerm, token);
        return new Result<PreVoteResult> { Term = response.Term, Value = (PreVoteResult)(int)response.Value };
    }

    public async Task<long?> SynchronizeAsync(long commitIndex, CancellationToken token)
    {
        if (!IsRemote)
        {
            return null;
        }

        var response = await VoteCallAsync(RaftVoteKind.Synchronize, 0, commitIndex, 0, token);
        return response.HasValue ? response.Value : null;
    }

    public async Task<Result<ReplicationStatus>> AppendEntriesAsync<TEntry, TList>(long term, TList entries, long prevLogIndex, long prevLogTerm, long commitIndex, CancellationToken token)
        where TEntry : IRaftLogEntry
        where TList : IReadOnlyList<TEntry>
    {
        if (!IsRemote)
        {
            return new Result<ReplicationStatus> { Term = term, Value = new ReplicationStatus { Result = HeartbeatResult.ReplicatedWithLeaderTerm, LastIndex = prevLogIndex + entries.Count } };
        }

        var count = entries.Count;
        IReadOnlyList<RaftWireEntry> wire;
        if (count == 0)
        {
            wire = [];
        }
        else
        {
            var array = new RaftWireEntry[count];
            for (var index = 0; index < count; index++)
            {
                var entry = entries[index];
                array[index] = new RaftWireEntry(entry.Term, entry.CommandId, entry.IsConfiguration, await PayloadAsync(entry, token));
            }

            wire = array;
        }

        var message = new RaftAppendMessage(context.Shard, RaftAppendKind.Append, context.LocalId, term, prevLogIndex, prevLogTerm, commitIndex, context.StateVersion, wire);
        var response = await CallAsync<RaftAppendMessage, RaftAppendResponse>(QuicMessageType.RaftAppendEntries, message, token);
        return new Result<ReplicationStatus>
        {
            Term = response.Term,
            Value = new ReplicationStatus { Result = (HeartbeatResult)response.Heartbeat, LastIndex = response.LastIndex },
        };
    }

    public async Task<Result<HeartbeatResult>> InstallSnapshotAsync(long term, IRaftLogEntry snapshot, long snapshotIndex, IDataTransferObject configuration, long configurationVersion, CancellationToken token)
    {
        if (!IsRemote)
        {
            return new Result<HeartbeatResult> { Term = term, Value = HeartbeatResult.ReplicatedWithLeaderTerm };
        }

        var payload = await PayloadAsync(snapshot, token);
        var message = new RaftSnapshotMessage(context.Shard, context.LocalId, term, snapshotIndex, context.StateVersion, new RaftWireEntry(snapshot.Term, snapshot.CommandId, snapshot.IsConfiguration, payload));
        var response = await CallAsync<RaftSnapshotMessage, RaftAppendResponse>(QuicMessageType.RaftInstallSnapshot, message, token);
        return new Result<HeartbeatResult> { Term = response.Term, Value = (HeartbeatResult)response.Heartbeat };
    }

    public async ValueTask CancelPendingRequestsAsync()
    {
        await _pending.CancelAsync();
    }

    public void Dispose() => _pending.Dispose();

    public override string ToString() => NodeId;

    private static async ValueTask<ReadOnlyMemory<byte>> PayloadAsync<TEntry>(TEntry entry, CancellationToken token)
        where TEntry : IRaftLogEntry
    {
        if (entry.TryGetMemory(out var memory))
        {
            return memory;
        }

        return await DataTransferObject.ToByteArrayAsync(entry, null, token);
    }

    private async Task<RaftVoteResponse> VoteCallAsync(RaftVoteKind kind, long term, long lastLogIndex, long lastLogTerm, CancellationToken token)
    {
        var message = new RaftVoteMessage(context.Shard, kind, context.LocalId, term, lastLogIndex, lastLogTerm, context.StateVersion);
        return IsRemote
            ? await CallAsync<RaftVoteMessage, RaftVoteResponse>(QuicMessageType.RaftRequestVote, message, token)
            : await Local().HandleVoteAsync(message, token);
    }

    private RaftShard Local() => context.Owner ?? throw new InvalidOperationException("The raft shard owning the local member is not started.");

    private async Task<TResponse> CallAsync<TRequest, TResponse>(ushort messageType, TRequest request, CancellationToken token)
        where TRequest : IBinaryMessage<TRequest>
        where TResponse : IBinaryMessage<TResponse>
    {
        using var linked = PooledRaftCts.CreateLinked(token, _pending.Token);
        var outcome = await context.Client.CallAsync<TRequest, TResponse>(NodeId, messageType, request, context.RequestTimeout, linked.Token);
        var (response, failure) = outcome switch
        {
            TResponse received => (received, (string?)null),
            QuicUnavailable unavailable => (default(TResponse), unavailable.Message),
            QuicTimeout timeout => (default(TResponse), timeout.Message),
            QuicRemoteError error => (default(TResponse), error.Message),
        };
        if (failure is not null || response is null)
        {
            ChangeStatus(ClusterMemberStatus.Unavailable);
            throw new MemberUnavailableException(this, failure ?? "empty response", new IOException(failure ?? "empty response"));
        }

        ChangeStatus(ClusterMemberStatus.Available);
        return response;
    }

    private void ChangeStatus(ClusterMemberStatus status)
    {
        var previous = (ClusterMemberStatus)Interlocked.Exchange(ref _status, (int)status);
        if (previous != status)
        {
            MemberStatusChanged?.Invoke(new ClusterMemberStatusChangedEventArgs<QuicRaftMember>(this, previous, status));
        }
    }
}

using DotNext.Net.Cluster;
using TerraScale.Contracts.Quic;
using TerraScale.QuicRpc;

namespace TerraBase.Cluster;

internal sealed class QuicRaftMember(ushort shard, ClusterMemberId localId, string nodeId, ClusterMemberId memberId, bool isRemote, QuicRpcClient client, TimeSpan requestTimeout) : IDisposable
{
    private readonly SemaphoreSlim _wake = new(0, 1);
    private int _signaled;

    public string NodeId { get; } = nodeId;

    public ClusterMemberId Id { get; } = memberId;

    public bool IsRemote { get; } = isRemote;

    public long NextIndex { get; set; } = 1;

    public long MatchIndex { get; set; }

    public long SentCommitIndex { get; set; }

    public void Signal()
    {
        if (Interlocked.Exchange(ref _signaled, 1) == 0)
        {
            _ = _wake.Release();
        }
    }

    public async Task<bool> WaitSignalAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var signaled = await _wake.WaitAsync(timeout, cancellationToken);
        if (signaled)
        {
            Volatile.Write(ref _signaled, 0);
        }

        return signaled;
    }

    public ValueTask<QuicCallOutcome<RaftVoteResponse>> SendVoteAsync(RaftVoteKind kind, long term, long lastLogIndex, long lastLogTerm, CancellationToken cancellationToken)
    {
        var message = new RaftVoteMessage(shard, kind, localId, term, lastLogIndex, lastLogTerm, 0);
        return client.CallAsync<RaftVoteMessage, RaftVoteResponse>(NodeId, QuicMessageType.RaftRequestVote, message, requestTimeout, cancellationToken);
    }

    public ValueTask<QuicCallOutcome<RaftAppendResponse>> SendAppendAsync(long term, long prevLogIndex, long prevLogTerm, long commitIndex, IReadOnlyList<RaftWireEntry> entries, CancellationToken cancellationToken)
    {
        var message = new RaftAppendMessage(shard, RaftAppendKind.Append, localId, term, prevLogIndex, prevLogTerm, commitIndex, 0, entries);
        return client.CallAsync<RaftAppendMessage, RaftAppendResponse>(NodeId, QuicMessageType.RaftAppendEntries, message, requestTimeout, cancellationToken);
    }

    public ValueTask<QuicCallOutcome<RaftAppendResponse>> SendInstallSnapshotAsync(long term, long snapshotIndex, long snapshotTerm, CancellationToken cancellationToken)
    {
        var message = new RaftSnapshotMessage(shard, localId, term, snapshotIndex, 0, new RaftWireEntry(snapshotTerm, null, false, ReadOnlyMemory<byte>.Empty));
        return client.CallAsync<RaftSnapshotMessage, RaftAppendResponse>(NodeId, QuicMessageType.RaftInstallSnapshot, message, requestTimeout, cancellationToken);
    }

    public void Dispose() => _wake.Dispose();
}

using System.Diagnostics;
using DotNext.Net.Cluster;
using DotNext.Net.Cluster.Consensus.Raft;
using Microsoft.Extensions.Logging;
using TerraScale.QuicRpc;

namespace TerraBase.Cluster;

internal enum RaftRole : byte
{
    Follower = 0,
    Candidate = 1,
    Leader = 2,
}

internal sealed class RaftShard : IAsyncDisposable
{
    private const int MaxEntriesPerAppend = 64;

    private readonly ushort _shard;
    private readonly QuicRaftMember _local;
    private readonly QuicRaftMember[] _peers;
    private readonly RaftShardLog _log;
    private readonly TimeSpan _electionLower;
    private readonly TimeSpan _electionUpper;
    private readonly TimeSpan _heartbeatInterval;
    private readonly int _quorum;
    private readonly ILogger _logger;
    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _applySignal = new(0, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly List<ProposalWaiter> _proposals = [];
    private readonly List<TaskCompletionSource<QuicRaftMember>> _leaderWaiters = [];

    private RaftRole _role = RaftRole.Follower;
    private QuicRaftMember? _leader;
    private long _commitIndex;
    private long _lastContactTimestamp;
    private int _applySignaled;
    private Task[] _loops = [];
    private int _disposed;

    public RaftShard(
        ushort shard,
        QuicRaftMember local,
        QuicRaftMember[] peers,
        RaftShardLog log,
        ClusterOptions options,
        ILogger logger)
    {
        _shard = shard;
        _local = local;
        _peers = peers;
        _log = log;
        _electionLower = options.RaftElectionTimeoutLower;
        _electionUpper = options.RaftElectionTimeoutUpper;
        var heartbeatMs = Math.Clamp((int)(options.RaftElectionTimeoutLower.TotalMilliseconds * 0.2), 10, 25);
        _heartbeatInterval = TimeSpan.FromMilliseconds(heartbeatMs);
        _quorum = ((1 + peers.Length) / 2) + 1;
        _logger = logger;
    }

    public ushort Shard => _shard;

    public long CurrentTerm => _log.Term;

    public QuicRaftMember? Leader => Volatile.Read(ref _leader);

    public bool IsLocalLeader => ReferenceEquals(Volatile.Read(ref _leader), _local);

    public void Start()
    {
        _log.Initialize();
        _commitIndex = _log.CommitIndex;
        var token = _shutdown.Token;
        var loops = new Task[_peers.Length + 2];
        loops[0] = RunElectionLoopAsync(token);
        loops[1] = RunApplyLoopAsync(token);
        for (var index = 0; index < _peers.Length; index++)
        {
            loops[index + 2] = RunPeerLoopAsync(_peers[index], token);
        }

        _loops = loops;
    }

    public ValueTask<QuicRaftMember?> WaitForLeaderAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _leader) is { } current)
        {
            return new(current);
        }

        return WaitForLeaderSlowAsync(timeout, cancellationToken);
    }

    public async ValueTask<RaftProposeStatus> ReplicateAsync(ReadOnlyMemory<byte> payload, CancellationToken deadline, CancellationToken callerToken)
    {
        long index;
        long term;
        TaskCompletionSource<RaftProposeStatus> completion;
        lock (_gate)
        {
            if (_role != RaftRole.Leader)
            {
                return RaftProposeStatus.NotLeader;
            }

            term = _log.Term;
            index = _log.AppendLocalMemory(term, payload);
            completion = new TaskCompletionSource<RaftProposeStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
            _proposals.Add(new ProposalWaiter(index, term, completion));
        }

        SignalAllPeers();
        if (deadline.IsCancellationRequested)
        {
            return RaftProposeStatus.Failed;
        }

        try
        {
            _ = await _log.FlushAppendedAsync(index, deadline);
        }
        catch (OperationCanceledException) when (!callerToken.IsCancellationRequested)
        {
            return RaftProposeStatus.Failed;
        }

        TryAdvanceCommitIndex();
        if (completion.Task.IsCompleted)
        {
            return await completion.Task;
        }

        using var registration = deadline.Register(static state =>
        {
            if (state is TaskCompletionSource<RaftProposeStatus> tcs)
            {
                _ = tcs.TrySetResult(RaftProposeStatus.Failed);
            }
        }, completion);
        return await completion.Task;
    }

    public ValueTask<RaftVoteResponse> HandleVoteAsync(RaftVoteMessage message, CancellationToken cancellationToken) => message.Kind switch
    {
        RaftVoteKind.Vote => HandleRequestVoteAsync(message, cancellationToken),
        RaftVoteKind.PreVote => new(HandlePreVote(message)),
        RaftVoteKind.Resign => new(HandleResign()),
        RaftVoteKind.Synchronize => new(new RaftVoteResponse(0, Volatile.Read(ref _commitIndex), true)),
        _ => throw new UnreachableException(),
    };

    public async ValueTask<RaftAppendResponse> HandleAppendAsync(RaftAppendMessage message, CancellationToken cancellationToken)
    {
        var currentTerm = _log.Term;
        if (message.Term < currentTerm)
        {
            return new RaftAppendResponse(currentTerm, (byte)HeartbeatResult.Rejected, _log.LastEntryIndex, RaftProposeStatus.Committed);
        }

        if (message.Term > currentTerm)
        {
            await StepDownAsync(message.Term, resetVote: true, cancellationToken);
        }
        else if (_role != RaftRole.Follower)
        {
            StepDownVolatile(message.Term);
        }

        RecordLeaderContact(message.Sender);
        var appended = await _log.TryAppendFromLeaderAsync(message.PrevLogIndex, message.PrevLogTerm, message.Entries, cancellationToken);
        if (!appended.Matched)
        {
            return new RaftAppendResponse(_log.Term, (byte)HeartbeatResult.Rejected, appended.LastIndex, RaftProposeStatus.Committed);
        }

        if (message.CommitIndex > Volatile.Read(ref _commitIndex))
        {
            var nextCommit = Math.Min(message.CommitIndex, appended.LastIndex);
            var changed = false;
            lock (_gate)
            {
                if (nextCommit > _commitIndex)
                {
                    Volatile.Write(ref _commitIndex, nextCommit);
                    changed = true;
                }
            }

            if (changed)
            {
                SignalApply();
            }
        }

        return new RaftAppendResponse(_log.Term, (byte)HeartbeatResult.ReplicatedWithLeaderTerm, appended.LastIndex, RaftProposeStatus.Committed);
    }

    public async ValueTask<RaftAppendResponse> HandleSnapshotAsync(RaftSnapshotMessage message, CancellationToken cancellationToken)
    {
        var currentTerm = _log.Term;
        if (message.Term < currentTerm)
        {
            return new RaftAppendResponse(currentTerm, (byte)HeartbeatResult.Rejected, 0, RaftProposeStatus.Committed);
        }

        if (message.Term > currentTerm)
        {
            await StepDownAsync(message.Term, resetVote: true, cancellationToken);
        }
        else if (_role != RaftRole.Follower)
        {
            StepDownVolatile(message.Term);
        }

        RecordLeaderContact(message.Sender);
        await _log.InstallSnapshotAsync(message.SnapshotIndex, message.Snapshot.Term, cancellationToken);
        lock (_gate)
        {
            if (message.SnapshotIndex > _commitIndex)
            {
                Volatile.Write(ref _commitIndex, message.SnapshotIndex);
            }
        }

        return new RaftAppendResponse(_log.Term, (byte)HeartbeatResult.ReplicatedWithLeaderTerm, message.SnapshotIndex, RaftProposeStatus.Committed);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _shutdown.CancelAsync();
        SignalAllPeers();
        SignalApply();
        FailAllProposals(RaftProposeStatus.Failed);
        CancelLeaderWaiters();
        if (_loops.Length > 0)
        {
            await Task.WhenAll(_loops).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }

        for (var index = 0; index < _peers.Length; index++)
        {
            _peers[index].Dispose();
        }

        _local.Dispose();
        _applySignal.Dispose();
        _shutdown.Dispose();
        await _log.DisposeAsync();
    }

    private async ValueTask<QuicRaftMember?> WaitForLeaderSlowAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        TaskCompletionSource<QuicRaftMember> waiter;
        lock (_gate)
        {
            if (_leader is { } current)
            {
                return current;
            }

            waiter = new TaskCompletionSource<QuicRaftMember>(TaskCreationOptions.RunContinuationsAsynchronously);
            _leaderWaiters.Add(waiter);
        }

        using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var delay = Task.Delay(timeout, delayCts.Token);
        var winner = await Task.WhenAny(waiter.Task, delay);
        await delayCts.CancelAsync();
        cancellationToken.ThrowIfCancellationRequested();
        return winner == waiter.Task && waiter.Task.IsCompletedSuccessfully ? await waiter.Task : null;
    }

    private RaftVoteResponse HandlePreVote(RaftVoteMessage message)
    {
        var currentTerm = _log.Term;
        if (message.Term < currentTerm || HasActiveLeaderLease())
        {
            return new RaftVoteResponse(currentTerm, (long)(_role == RaftRole.Leader ? PreVoteResult.RejectedByLeader : PreVoteResult.RejectedByFollower), true);
        }

        var accepted = _log.IsUpToDate(message.LastLogIndex, message.LastLogTerm);
        return new RaftVoteResponse(currentTerm, (long)(accepted ? PreVoteResult.Accepted : PreVoteResult.RejectedByFollower), true);
    }

    private async ValueTask<RaftVoteResponse> HandleRequestVoteAsync(RaftVoteMessage message, CancellationToken cancellationToken)
    {
        var currentTerm = _log.Term;
        if (message.Term < currentTerm)
        {
            return new RaftVoteResponse(currentTerm, 0, true);
        }

        if (message.Term > currentTerm)
        {
            StepDownVolatile(message.Term);
        }

        var granted = await _log.TryGrantVoteAsync(message.Term, message.Sender, message.LastLogIndex, message.LastLogTerm, cancellationToken);
        if (granted)
        {
            Volatile.Write(ref _lastContactTimestamp, Stopwatch.GetTimestamp());
        }

        return new RaftVoteResponse(_log.Term, granted ? 1 : 0, true);
    }

    private RaftVoteResponse HandleResign()
    {
        lock (_gate)
        {
            if (_role != RaftRole.Leader)
            {
                return new RaftVoteResponse(0, 0, true);
            }

            _role = RaftRole.Follower;
            Volatile.Write(ref _leader, null);
            FailAllProposalsLocked(RaftProposeStatus.NotLeader);
            return new RaftVoteResponse(0, 1, true);
        }
    }

    private bool HasActiveLeaderLease()
    {
        var last = Volatile.Read(ref _lastContactTimestamp);
        return last != 0 && Stopwatch.GetElapsedTime(last) < _electionLower;
    }

    private void RecordLeaderContact(ClusterMemberId senderId)
    {
        Volatile.Write(ref _lastContactTimestamp, Stopwatch.GetTimestamp());
        var current = Volatile.Read(ref _leader);
        if (current is not null && current.Id == senderId)
        {
            return;
        }

        var member = FindMember(senderId);
        if (member is null)
        {
            return;
        }

        lock (_gate)
        {
            Volatile.Write(ref _leader, member);
            CompleteLeaderWaitersLocked(member);
        }
    }

    private QuicRaftMember? FindMember(ClusterMemberId id)
    {
        if (_local.Id == id)
        {
            return _local;
        }

        for (var index = 0; index < _peers.Length; index++)
        {
            if (_peers[index].Id == id)
            {
                return _peers[index];
            }
        }

        return null;
    }

    private void StepDownVolatile(long newTerm)
    {
        lock (_gate)
        {
            if (newTerm >= _log.Term && _role != RaftRole.Follower)
            {
                _role = RaftRole.Follower;
                Volatile.Write(ref _leader, null);
                FailAllProposalsLocked(RaftProposeStatus.NotLeader);
            }
        }
    }

    private async ValueTask StepDownAsync(long newTerm, bool resetVote, CancellationToken cancellationToken)
    {
        StepDownVolatile(newTerm);
        await _log.UpdateTermAsync(newTerm, resetVote, cancellationToken);
    }

    private async Task RunElectionLoopAsync(CancellationToken cancellationToken)
    {
        var lowerMs = (int)_electionLower.TotalMilliseconds;
        var upperMs = Math.Max(lowerMs + 1, (int)_electionUpper.TotalMilliseconds);
        while (!cancellationToken.IsCancellationRequested)
        {
            var timeoutMs = Random.Shared.Next(lowerMs, upperMs);
            await Task.Delay(timeoutMs, cancellationToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            if (_role == RaftRole.Leader)
            {
                continue;
            }

            var lastContact = Volatile.Read(ref _lastContactTimestamp);
            if (lastContact != 0 && Stopwatch.GetElapsedTime(lastContact).TotalMilliseconds < timeoutMs)
            {
                continue;
            }

            await RunElectionRoundAsync(cancellationToken);
        }
    }

    private async Task RunElectionRoundAsync(CancellationToken cancellationToken)
    {
        var (lastIndex, lastTerm) = _log.LastIndexAndTerm();
        var preVoteTerm = _log.Term + 1;
        var preVotes = 1;
        var preTasks = new Task<QuicCallOutcome<RaftVoteResponse>>[_peers.Length];
        for (var index = 0; index < _peers.Length; index++)
        {
            preTasks[index] = _peers[index].SendVoteAsync(RaftVoteKind.PreVote, preVoteTerm, lastIndex, lastTerm, cancellationToken).AsTask();
        }

        await foreach (var completed in Task.WhenEach(preTasks).WithCancellation(cancellationToken))
        {
            var outcome = await completed;
            if (outcome switch { RaftVoteResponse r => r, QuicUnavailable or QuicTimeout or QuicRemoteError => null } is { } resp)
            {
                if (resp.Term > _log.Term)
                {
                    await StepDownAsync(resp.Term, resetVote: true, cancellationToken);
                    return;
                }

                if ((PreVoteResult)(int)resp.Value == PreVoteResult.Accepted)
                {
                    preVotes++;
                    if (preVotes >= _quorum)
                    {
                        break;
                    }
                }
            }
        }

        if (preVotes < _quorum || cancellationToken.IsCancellationRequested)
        {
            return;
        }

        var electionTerm = await _log.IncrementTermAndVoteSelfAsync(_local.Id, cancellationToken);
        lock (_gate)
        {
            if (_log.Term != electionTerm)
            {
                return;
            }

            _role = RaftRole.Candidate;
            Volatile.Write(ref _leader, null);
        }

        Volatile.Write(ref _lastContactTimestamp, Stopwatch.GetTimestamp());
        (lastIndex, lastTerm) = _log.LastIndexAndTerm();
        var voteTasks = new Task<QuicCallOutcome<RaftVoteResponse>>[_peers.Length];
        for (var index = 0; index < _peers.Length; index++)
        {
            voteTasks[index] = _peers[index].SendVoteAsync(RaftVoteKind.Vote, electionTerm, lastIndex, lastTerm, cancellationToken).AsTask();
        }

        var granted = 1;
        await foreach (var completed in Task.WhenEach(voteTasks).WithCancellation(cancellationToken))
        {
            var outcome = await completed;
            if (outcome switch { RaftVoteResponse r => r, QuicUnavailable or QuicTimeout or QuicRemoteError => null } is { } resp)
            {
                if (resp.Term > electionTerm)
                {
                    await StepDownAsync(resp.Term, resetVote: true, cancellationToken);
                    return;
                }

                if (resp.Value != 0)
                {
                    granted++;
                    if (granted >= _quorum)
                    {
                        await BecomeLeaderAsync(electionTerm, cancellationToken);
                        return;
                    }
                }
            }
        }
    }

    private async ValueTask BecomeLeaderAsync(long electionTerm, CancellationToken cancellationToken)
    {
        long noopIndex;
        lock (_gate)
        {
            if (_role != RaftRole.Candidate || _log.Term != electionTerm)
            {
                return;
            }

            _role = RaftRole.Leader;
            Volatile.Write(ref _leader, _local);
            var nextStart = _log.LastEntryIndex + 1;
            for (var index = 0; index < _peers.Length; index++)
            {
                _peers[index].NextIndex = nextStart;
                _peers[index].MatchIndex = 0;
                _peers[index].SentCommitIndex = 0;
            }

            noopIndex = _log.AppendLocalMemory(electionTerm, ReadOnlyMemory<byte>.Empty);
            CompleteLeaderWaitersLocked(_local);
        }

        SignalAllPeers();
        _ = await _log.FlushAppendedAsync(noopIndex, cancellationToken);
        TryAdvanceCommitIndex();
    }

    private async Task RunPeerLoopAsync(QuicRaftMember peer, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            if (_role != RaftRole.Leader)
            {
                await ((Task)peer.WaitSignalAsync(_heartbeatInterval, cancellationToken)).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                continue;
            }

            var term = _log.Term;
            var commitIndex = Volatile.Read(ref _commitIndex);
            var hasEntries = _log.TryReadForPeer(peer.NextIndex, MaxEntriesPerAppend, out var prevLogIndex, out var prevLogTerm, out var entries);
            QuicCallOutcome<RaftAppendResponse> outcome;
            long sentLastIndex;
            if (!hasEntries)
            {
                sentLastIndex = prevLogIndex;
                outcome = await peer.SendInstallSnapshotAsync(term, prevLogIndex, prevLogTerm, cancellationToken);
            }
            else
            {
                sentLastIndex = prevLogIndex + entries.Count;
                outcome = await peer.SendAppendAsync(term, prevLogIndex, prevLogTerm, commitIndex, entries, cancellationToken);
            }

            var response = outcome switch
            {
                RaftAppendResponse received => received,
                QuicUnavailable or QuicTimeout or QuicRemoteError => null,
            };
            if (response is null)
            {
                await ((Task)peer.WaitSignalAsync(_heartbeatInterval, cancellationToken)).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                continue;
            }

            if (response.Term > term)
            {
                await StepDownAsync(response.Term, resetVote: true, cancellationToken);
                continue;
            }

            if (_role != RaftRole.Leader || _log.Term != term)
            {
                continue;
            }

            var heartbeat = (HeartbeatResult)response.Heartbeat;
            if (heartbeat is HeartbeatResult.ReplicatedWithLeaderTerm or HeartbeatResult.Replicated)
            {
                var advancedMatch = false;
                lock (_gate)
                {
                    if (_role == RaftRole.Leader && _log.Term == term)
                    {
                        if (sentLastIndex > peer.MatchIndex)
                        {
                            peer.MatchIndex = sentLastIndex;
                            advancedMatch = true;
                        }

                        if (sentLastIndex + 1 > peer.NextIndex)
                        {
                            peer.NextIndex = sentLastIndex + 1;
                        }

                        if (commitIndex > peer.SentCommitIndex)
                        {
                            peer.SentCommitIndex = commitIndex;
                        }
                    }
                }

                if (advancedMatch)
                {
                    TryAdvanceCommitIndex();
                }

                if (peer.NextIndex <= _log.LastEntryIndex)
                {
                    continue;
                }

                await ((Task)peer.WaitSignalAsync(_heartbeatInterval, cancellationToken)).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
            else
            {
                lock (_gate)
                {
                    if (_role == RaftRole.Leader && _log.Term == term)
                    {
                        var candidateNext = Math.Min(peer.NextIndex - 1, response.LastIndex + 1);
                        peer.NextIndex = Math.Max(1, candidateNext);
                    }
                }
            }
        }
    }

    private void TryAdvanceCommitIndex()
    {
        var advanced = false;
        lock (_gate)
        {
            if (_role != RaftRole.Leader)
            {
                return;
            }

            var currentTerm = _log.Term;
            var localFlushed = _log.FlushedIndex;
            if (localFlushed <= _commitIndex)
            {
                return;
            }

            var best = localFlushed;
            for (var index = 0; index < _peers.Length; index++)
            {
                var candidate = Math.Min(_peers[index].MatchIndex, localFlushed);
                if (candidate <= _commitIndex)
                {
                    continue;
                }

                var count = 1;
                for (var peerIndex = 0; peerIndex < _peers.Length; peerIndex++)
                {
                    if (_peers[peerIndex].MatchIndex >= candidate)
                    {
                        count++;
                    }
                }

                if (count >= _quorum && _log.TermAt(candidate) == currentTerm)
                {
                    if (!advanced || candidate > best)
                    {
                        best = candidate;
                        advanced = true;
                    }
                }
            }

            if (advanced && best > _commitIndex)
            {
                Volatile.Write(ref _commitIndex, best);
                CompleteCommittedProposalsLocked(best, currentTerm);
            }
            else
            {
                advanced = false;
            }
        }

        if (advanced)
        {
            SignalApply();
        }
    }

    private async Task RunApplyLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var target = Volatile.Read(ref _commitIndex);
            if (target > _log.CommitIndex)
            {
                try
                {
                    await _log.ApplyUpToAsync(target, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception exception)
                {
                    ClusterLog.LoopFailed(_logger, "raft-apply", TimeSpan.FromMilliseconds(50), exception);
                    await Task.Delay(50, cancellationToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                }

                continue;
            }

            var waitTask = _applySignal.WaitAsync(_heartbeatInterval, cancellationToken);
            await ((Task)waitTask).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            if (waitTask.IsCompletedSuccessfully && await waitTask)
            {
                Volatile.Write(ref _applySignaled, 0);
            }
        }
    }

    private void SignalAllPeers()
    {
        for (var index = 0; index < _peers.Length; index++)
        {
            _peers[index].Signal();
        }
    }

    private void SignalApply()
    {
        if (Interlocked.Exchange(ref _applySignaled, 1) == 0)
        {
            _ = _applySignal.Release();
        }
    }

    private void CompleteCommittedProposalsLocked(long commitIndex, long currentTerm)
    {
        for (var index = _proposals.Count - 1; index >= 0; index--)
        {
            var waiter = _proposals[index];
            if (waiter.LogIndex <= commitIndex)
            {
                _ = waiter.Completion.TrySetResult(waiter.Term == currentTerm ? RaftProposeStatus.Committed : RaftProposeStatus.NotLeader);
                var last = _proposals.Count - 1;
                _proposals[index] = _proposals[last];
                _proposals.RemoveAt(last);
            }
        }
    }

    private void FailAllProposals(RaftProposeStatus status)
    {
        lock (_gate)
        {
            FailAllProposalsLocked(status);
        }
    }

    private void FailAllProposalsLocked(RaftProposeStatus status)
    {
        foreach (var waiter in _proposals)
        {
            _ = waiter.Completion.TrySetResult(status);
        }

        _proposals.Clear();
    }

    private void CompleteLeaderWaitersLocked(QuicRaftMember leader)
    {
        foreach (var waiter in _leaderWaiters)
        {
            _ = waiter.TrySetResult(leader);
        }

        _leaderWaiters.Clear();
    }

    private void CancelLeaderWaiters()
    {
        lock (_gate)
        {
            foreach (var waiter in _leaderWaiters)
            {
                _ = waiter.TrySetCanceled();
            }

            _leaderWaiters.Clear();
        }
    }

    private readonly record struct ProposalWaiter(long LogIndex, long Term, TaskCompletionSource<RaftProposeStatus> Completion);
}

using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging;
using TerraBase.Storage;
using TerraScale.Contracts.Quic;
using TerraScale.QuicRpc;

namespace TerraBase.Cluster;

public readonly union StrongVote(Prepared, Wounded, ReadSetStale, SchemaEpochStale, ClusterUnavailable);

public sealed record StrongVoteTally(int Prepared, int Required, IReadOnlyList<StrongPrepareOutcome> Rejections, int Unreachable, IReadOnlyList<string> RemoteParticipants, long LeaseDeadline, bool WithinLease);

public sealed class StrongVoteCollector(ClusterOptions options, ClusterTopology topology, PeerHealthMonitor health, StrongParticipant participant, QuicRpcClient client, TimeProvider timeProvider)
{
    internal ValueTask<StrongVoteTally> CollectAsync(ISqlDatabaseSession session, StrongPreparePayload payload, StrongTransactionHandle handle, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(payload);
        var started = timeProvider.GetTimestamp();
        var leaseDeadline = started + (long)(options.StrongLockLease.TotalSeconds * timeProvider.TimestampFrequency);
        var regions = topology.Regions;
        var regionCount = regions.Count;
        var required = topology.RegionQuorum;
        return regionCount == 1
            ? CollectSingleRegionAsync(session, payload, handle, regions[0], required, started, leaseDeadline, cancellationToken)
            : CollectMultiRegionAsync(session, payload, handle, regions, required, started, leaseDeadline, cancellationToken);
    }

    private async ValueTask<StrongVoteTally> CollectSingleRegionAsync(
        ISqlDatabaseSession session,
        StrongPreparePayload payload,
        StrongTransactionHandle handle,
        string region,
        int required,
        long started,
        long leaseDeadline,
        CancellationToken cancellationToken)
    {
        var candidates = topology.StrongCandidates(region, payload.DatabaseId);
        var ordered = OrderCandidates(candidates);
        StrongVote last = new ClusterUnavailable("no candidate available in region", "region_unreachable");
        string? firstRemote = null;
        List<string>? moreRemotes = null;
        for (var index = 0; index < ordered.Count; index++)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            var candidate = ordered[index];
            if (!candidate.IsLocal)
            {
                handle.DispatchTo(candidate.NodeId);
                if (firstRemote is null)
                {
                    firstRemote = candidate.NodeId;
                }
                else if (!string.Equals(firstRemote, candidate.NodeId, StringComparison.Ordinal))
                {
                    moreRemotes ??= [firstRemote];
                    if (!ContainsNode(moreRemotes, candidate.NodeId))
                    {
                        moreRemotes.Add(candidate.NodeId);
                    }
                }
            }

            var vote = await VoteAsync(session, payload, candidate, cancellationToken);
            last = vote;
            if (!IsRetryable(vote))
            {
                break;
            }
        }

        var (prepared, unreachable, rejections) = TallySingleVote(last);
        IReadOnlyList<string> remoteParticipants = moreRemotes is not null
            ? moreRemotes
            : firstRemote is not null ? [firstRemote] : [];
        var withinLease = timeProvider.GetElapsedTime(started) < options.StrongLockLease / 2;
        return new StrongVoteTally(prepared, required, rejections, unreachable, remoteParticipants, leaseDeadline, withinLease);
    }

    private static (int Prepared, int Unreachable, IReadOnlyList<StrongPrepareOutcome> Rejections) TallySingleVote(StrongVote vote) => vote switch
    {
        Prepared => (1, 0, []),
        Wounded wounded => (0, 0, [wounded]),
        ReadSetStale stale => (0, 0, [stale]),
        SchemaEpochStale epoch => (0, 0, [epoch]),
        ClusterUnavailable => (0, 1, []),
    };

    private async ValueTask<StrongVoteTally> CollectMultiRegionAsync(
        ISqlDatabaseSession session,
        StrongPreparePayload payload,
        StrongTransactionHandle handle,
        IReadOnlyList<string> regions,
        int required,
        long started,
        long leaseDeadline,
        CancellationToken cancellationToken)
    {
        var regionCount = regions.Count;
        var voteCancel = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var dispatch = new VoteDispatchState();
        var votes = new Task<RegionVote>[regionCount];
        for (var index = 0; index < regionCount; index++)
        {
            votes[index] = VoteRegionAsync(session, payload, handle, regions[index], dispatch, voteCancel.Token);
        }

        var prepared = 0;
        var unreachable = 0;
        List<StrongPrepareOutcome>? rejections = null;
        try
        {
            if (regionCount <= 4)
            {
                var remaining = regionCount;
                var completedMask = 0;
                while (remaining > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var completedIndex = -1;
                    for (var index = 0; index < regionCount; index++)
                    {
                        if ((completedMask & (1 << index)) == 0 && votes[index].IsCompleted)
                        {
                            completedIndex = index;
                            break;
                        }
                    }

                    RegionVote result;
                    if (completedIndex >= 0)
                    {
                        completedMask |= 1 << completedIndex;
                        remaining--;
                        result = await votes[completedIndex];
                    }
                    else if (remaining == 1)
                    {
                        var lastIndex = FirstPendingIndex(regionCount, completedMask);
                        completedMask |= 1 << lastIndex;
                        remaining = 0;
                        result = await votes[lastIndex].WaitAsync(cancellationToken);
                    }
                    else if (remaining == 2)
                    {
                        var firstIndex = FirstPendingIndex(regionCount, completedMask);
                        var secondIndex = FirstPendingIndex(regionCount, completedMask | (1 << firstIndex));
                        var finished = await Task.WhenAny(votes[firstIndex], votes[secondIndex]).WaitAsync(cancellationToken);
                        var finishedIndex = ReferenceEquals(finished, votes[firstIndex]) ? firstIndex : secondIndex;
                        completedMask |= 1 << finishedIndex;
                        remaining--;
                        result = await finished;
                    }
                    else
                    {
                        Task<RegionVote>[] pendingTasks;
                        if (remaining == regionCount)
                        {
                            pendingTasks = votes;
                        }
                        else
                        {
                            pendingTasks = new Task<RegionVote>[remaining];
                            var write = 0;
                            for (var index = 0; index < regionCount; index++)
                            {
                                if ((completedMask & (1 << index)) == 0)
                                {
                                    pendingTasks[write++] = votes[index];
                                }
                            }
                        }

                        var finished = await Task.WhenAny(pendingTasks).WaitAsync(cancellationToken);
                        for (var index = 0; index < regionCount; index++)
                        {
                            if ((completedMask & (1 << index)) == 0 && ReferenceEquals(votes[index], finished))
                            {
                                completedMask |= 1 << index;
                                remaining--;
                                break;
                            }
                        }

                        result = await finished;
                    }

                    if (TallyVote(result.Vote, regionCount, required, ref prepared, ref unreachable, ref rejections))
                    {
                        await voteCancel.CancelAsync();
                        break;
                    }
                }
            }
            else
            {
                await foreach (var completed in Task.WhenEach(votes).WithCancellation(cancellationToken))
                {
                    var result = await completed;
                    if (TallyVote(result.Vote, regionCount, required, ref prepared, ref unreachable, ref rejections))
                    {
                        await voteCancel.CancelAsync();
                        break;
                    }
                }
            }
        }
        finally
        {
            await voteCancel.CancelAsync();
            var allCompleted = true;
            for (var index = 0; index < votes.Length; index++)
            {
                if (!votes[index].IsCompleted)
                {
                    allCompleted = false;
                    break;
                }
            }

            if (allCompleted)
            {
                for (var index = 0; index < votes.Length; index++)
                {
                    _ = votes[index].Exception;
                }

                voteCancel.Dispose();
            }
            else
            {
                _ = Task.WhenAll(votes).ContinueWith(
                    static (task, state) =>
                    {
                        _ = task.Exception;
                        if (state is CancellationTokenSource source)
                        {
                            source.Dispose();
                        }
                    },
                    voteCancel,
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
        }

        var remoteParticipants = dispatch.CloseAndSnapshot();
        var withinLease = timeProvider.GetElapsedTime(started) < options.StrongLockLease / 2;
        return new StrongVoteTally(prepared, required, rejections is not null ? rejections : [], unreachable, remoteParticipants, leaseDeadline, withinLease);
    }

    private static bool TallyVote(
        StrongVote vote,
        int regionCount,
        int required,
        ref int prepared,
        ref int unreachable,
        ref List<StrongPrepareOutcome>? rejections)
    {
        var definitive = vote switch
        {
            Prepared => Increment(ref prepared),
            Wounded wounded => Reject(ref rejections, wounded, definitive: false),
            ReadSetStale stale => Reject(ref rejections, stale, definitive: true),
            SchemaEpochStale epoch => Reject(ref rejections, epoch, definitive: true),
            ClusterUnavailable => Increment(ref unreachable),
        };
        var rejectionCount = rejections?.Count ?? 0;
        return prepared >= required || definitive || regionCount - rejectionCount - unreachable < required;
    }

    private static int FirstPendingIndex(int regionCount, int completedMask)
    {
        for (var index = 0; index < regionCount; index++)
        {
            if ((completedMask & (1 << index)) == 0)
            {
                return index;
            }
        }

        throw new UnreachableException();
    }

    private static bool ContainsNode(List<string> nodes, string nodeId)
    {
        for (var index = 0; index < nodes.Count; index++)
        {
            if (string.Equals(nodes[index], nodeId, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool SetSingleRejection(ref IReadOnlyList<StrongPrepareOutcome> rejections, StrongPrepareOutcome outcome)
    {
        rejections = [outcome];
        return true;
    }

    private async Task<RegionVote> VoteRegionAsync(
        ISqlDatabaseSession session,
        StrongPreparePayload payload,
        StrongTransactionHandle handle,
        string region,
        VoteDispatchState dispatch,
        CancellationToken cancellationToken)
    {
        var candidates = topology.StrongCandidates(region, payload.DatabaseId);
        var ordered = OrderCandidates(candidates);
        StrongVote last = new ClusterUnavailable("no candidate available in region", "region_unreachable");
        for (var index = 0; index < ordered.Count; index++)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            var candidate = ordered[index];
            if (!candidate.IsLocal && !dispatch.TryRecord(handle, candidate.NodeId, cancellationToken))
            {
                break;
            }

            var vote = await VoteAsync(session, payload, candidate, cancellationToken);
            last = vote;
            if (!IsRetryable(vote))
            {
                return new RegionVote(vote);
            }
        }

        return new RegionVote(last);
    }

    private IReadOnlyList<StrongParticipantTarget> OrderCandidates(IReadOnlyList<StrongParticipantTarget> candidates)
    {
        var count = candidates.Count;
        if (count <= 1)
        {
            return candidates;
        }

        var seenUnreachable = false;
        var needsReorder = false;
        for (var index = 0; index < count; index++)
        {
            var candidate = candidates[index];
            var reachable = candidate.IsLocal || health.IsReachable(candidate.NodeId);
            if (!reachable)
            {
                seenUnreachable = true;
            }
            else if (seenUnreachable)
            {
                needsReorder = true;
                break;
            }
        }

        if (!needsReorder)
        {
            return candidates;
        }

        var ordered = new StrongParticipantTarget[count];
        var write = 0;
        for (var index = 0; index < count; index++)
        {
            var candidate = candidates[index];
            if (candidate.IsLocal || health.IsReachable(candidate.NodeId))
            {
                ordered[write++] = candidate;
            }
        }

        for (var index = 0; index < count; index++)
        {
            var candidate = candidates[index];
            if (!candidate.IsLocal && !health.IsReachable(candidate.NodeId))
            {
                ordered[write++] = candidate;
            }
        }

        return ordered;
    }

    private static bool IsRetryable(StrongVote vote) => vote switch
    {
        ClusterUnavailable => true,
        Prepared or Wounded or ReadSetStale or SchemaEpochStale => false,
    };

    private readonly record struct RegionVote(StrongVote Vote);

    private sealed class VoteDispatchState
    {
        private readonly Lock _gate = new();
        private HashSet<string>? _dispatched;
        private bool _closed;

        public bool TryRecord(StrongTransactionHandle handle, string nodeId, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                if (_closed || cancellationToken.IsCancellationRequested)
                {
                    return false;
                }

                handle.DispatchTo(nodeId);
                _dispatched ??= new HashSet<string>(StringComparer.Ordinal);
                _ = _dispatched.Add(nodeId);
                return true;
            }
        }

        public string[] CloseAndSnapshot()
        {
            lock (_gate)
            {
                _closed = true;
                if (_dispatched is null || _dispatched.Count == 0)
                {
                    return [];
                }

                var result = new string[_dispatched.Count];
                _dispatched.CopyTo(result);
                return result;
            }
        }
    }

    private static bool Increment(ref int counter)
    {
        counter++;
        return false;
    }

    private static bool Reject(ref List<StrongPrepareOutcome>? rejections, StrongPrepareOutcome outcome, bool definitive)
    {
        rejections ??= [];
        rejections.Add(outcome);
        return definitive;
    }

    private static StrongVote Flatten(StrongPrepareOutcome outcome) => outcome switch
    {
        Prepared prepared => prepared,
        Wounded wounded => wounded,
        ReadSetStale stale => stale,
        SchemaEpochStale epoch => epoch,
    };

    private async ValueTask<StrongVote> VoteAsync(ISqlDatabaseSession session, StrongPreparePayload payload, StrongParticipantTarget target, CancellationToken cancellationToken)
    {
        if (target.IsLocal)
        {
            return await participant.PrepareAsync(session, payload, options.NodeId, CausalBookmark.Empty, cancellationToken);
        }

        var response = await client.CallAsync<StrongPrepareMessage, StrongPrepareResponseMessage>(
            target.NodeId,
            QuicMessageType.StrongPrepare,
            new StrongPrepareMessage(payload, options.NodeId, session.CurrentBookmark),
            options.StrongPrepareTimeout,
            cancellationToken);
        return response switch
        {
            StrongPrepareResponseMessage message => Flatten(message.Outcome),
            QuicUnavailable unavailable => new ClusterUnavailable(unavailable.Message, unavailable.Code),
            QuicTimeout timeout => TimedOutVote(target.NodeId, timeout),
            QuicRemoteError error => new ClusterUnavailable(error.Message, error.Code),
        };
    }

    private ClusterUnavailable TimedOutVote(string nodeId, QuicTimeout timeout)
    {
        client.ResetPeer(nodeId);
        return new ClusterUnavailable(timeout.Message, timeout.Code);
    }
}

public sealed class StrongCoordinator(ClusterTopology topology, ClusterDatabases databases, StrongVoteCollector votes, StrongTransactionRegistry registry, CommitFinalizer finalizer, AntiEntropyService antiEntropy, StrongCommitRecovery recovery, HlcClock clock, TimeProvider timeProvider, ILogger<StrongCoordinator> logger)
{
    private const string WoundedReason = "wounded";

    private static readonly TimeSpan SiblingSyncTimeout = TimeSpan.FromMilliseconds(500);

    public async ValueTask<ClusterWriteOutcome> ExecuteAsync(ISqlDatabaseSession session, SqlStatementRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(request);
        if (topology.RegionPeers.Count > 0 && topology.StrongCandidates(topology.LocalRegion, session.DatabaseId) is [{ IsLocal: false }, ..])
        {
            _ = await antiEntropy.SyncInRegionAsync(session.DatabaseId, SiblingSyncTimeout, cancellationToken);
        }

        var staged = await session.StageWriteAsync(request with { Mode = ConsistencyMode.Strong }, cancellationToken);
        return staged switch
        {
            StagedWrite write => Written(write, await CommitAsync(session, write, cancellationToken)),
            SqlError error => error,
            SchemaMismatch mismatch => mismatch,
            StorageBusy busy => busy,
            StorageTimeout timeout => new ClusterTimeout(timeout.Message, timeout.Code),
        };
    }

    public async ValueTask<ClusterCommitTransactionOutcome> CommitTransactionAsync(ISqlDatabaseSession session, ISqlTransaction transaction, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(transaction);
        var staged = await transaction.StageAsync(cancellationToken);
        return staged switch
        {
            StagedWrite write => await CommitAsync(session, write, cancellationToken),
            SchemaMismatch mismatch => mismatch,
            StorageBusy busy => busy,
            StorageTimeout timeout => new ClusterTimeout(timeout.Message, timeout.Code),
        };
    }

    public async ValueTask<ClusterCommitTransactionOutcome> CommitAsync(ISqlDatabaseSession session, StagedWrite staged, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(staged);
        if (staged.WriteKeys.Count == 0 && !staged.IsSchemaChange && staged.DeltaPayload.IsEmpty)
        {
            var local = await session.CommitStagedAsync(staged.Token, clock.Tick(), leaseDeadline: null, cancellationToken);
            return local switch
            {
                CommitMetadata commit => commit,
                StagedWriteMissing => Conflict(staged, "staged_write_expired"),
                ReadSetConflict => Conflict(staged, "read_set_stale"),
                SchemaMismatch mismatch => mismatch,
                StorageBusy busy => busy,
                StorageTimeout timeout => new ClusterTimeout(timeout.Message),
            };
        }

        var payload = new StrongPreparePayload(
            session.DatabaseId,
            staged.TxId,
            topology.LocalRegion,
            clock.Tick(),
            staged.ReadSnapshot,
            staged.SchemaEpoch,
            staged.ReadSet,
            staged.ScannedTables,
            staged.WriteKeys,
            staged.IsSchemaChange);
        var handle = registry.Register(payload);
        var tally = await votes.CollectAsync(session, payload, handle, cancellationToken);
        if (tally.Prepared < tally.Required || !tally.WithinLease || !handle.TryBeginCommit())
        {
            var reason = Rejection(session.DatabaseId, staged, tally, handle);
            CatchUpOnStaleVotes(session.DatabaseId, tally);
            await AbortAsync(session, staged, handle, payload, tally, AbortReason(reason));
            return reason;
        }

        if (!recovery.TryReserve())
        {
            await AbortAsync(session, staged, handle, payload, tally, "strong_commit_capacity");
            return new ClusterUnavailable("strong commit recovery is at capacity", "strong_commit_capacity");
        }

        var deferred = false;
        CommitStagedOutcome committed;
        try
        {
            var now = timeProvider.GetTimestamp();
            if (now >= tally.LeaseDeadline)
            {
                await AbortAsync(session, staged, handle, payload, tally, "lease_expired");
                return new ClusterTimeout("participant lock leases expired before the staged commit");
            }

            var commitValueTask = session.CommitStagedAsync(staged.Token, clock.Tick(), tally.LeaseDeadline, CancellationToken.None);
            if (commitValueTask.IsCompletedSuccessfully)
            {
                committed = await commitValueTask;
            }
            else
            {
                using var timer = new CancellationTokenSource();
                var elapsed = Task.Delay(timeProvider.GetElapsedTime(now, tally.LeaseDeadline), timeProvider, timer.Token);
                var completion = commitValueTask.AsTask();
                if (await Task.WhenAny(completion, elapsed) != completion && !completion.IsCompletedSuccessfully)
                {
                    recovery.Enqueue(() => ResolveExpiredCommitAsync(session, staged, handle, payload, tally, completion));
                    deferred = true;
                    return new ClusterUnavailable("the staged commit's outcome could not be confirmed before its participant leases expired", "write_outcome_unknown");
                }

                await timer.CancelAsync();
                committed = await completion;
            }
        }
        finally
        {
            if (!deferred)
            {
                recovery.Release();
            }
        }

        return await ResolveCommitAsync(session, staged, handle, payload, tally, committed);
    }

    private async Task ResolveExpiredCommitAsync(
        ISqlDatabaseSession session, StagedWrite staged, StrongTransactionHandle handle, StrongPreparePayload payload, StrongVoteTally tally, Task<CommitStagedOutcome> completion)
    {
        var outcome = await ResolveCommitAsync(session, staged, handle, payload, tally, await completion);
        _ = outcome switch
        {
            CommitMetadata => true,
            SerializationConflict or SchemaMismatch or ClusterTimeout or ClusterUnavailable or StorageBusy => false,
        };
    }

    private async ValueTask<ClusterCommitTransactionOutcome> ResolveCommitAsync(
        ISqlDatabaseSession session, StagedWrite staged, StrongTransactionHandle handle, StrongPreparePayload payload, StrongVoteTally tally, CommitStagedOutcome committed)
    {
        var finalized = committed switch
        {
            CommitMetadata commit => commit,
            StagedWriteMissing or ReadSetConflict or SchemaMismatch or StorageBusy or StorageTimeout => null,
        };
        if (finalized is { } completeCommit)
        {
            registry.Complete(handle, committed: true);
            await finalizer.FinalizeStrongAsync(session.DatabaseId, completeCommit, CancellationToken.None);
            return completeCommit;
        }

        ClusterCommitTransactionOutcome failed = committed switch
        {
            CommitMetadata completed => completed,
            StagedWriteMissing => Conflict(staged, "staged_write_expired"),
            ReadSetConflict => Conflict(staged, "read_set_stale"),
            SchemaMismatch mismatch => mismatch,
            StorageBusy busy => busy,
            StorageTimeout storageTimeout => new ClusterTimeout(storageTimeout.Message),
        };
        await AbortAsync(session, staged, handle, payload, tally, AbortReason(failed));
        return failed;
    }

    private void CatchUpOnStaleVotes(Ulid databaseId, StrongVoteTally tally)
    {
        for (var index = 0; index < tally.Rejections.Count; index++)
        {
            var stale = tally.Rejections[index] switch
            {
                ReadSetStale or SchemaEpochStale => true,
                Prepared or Wounded => false,
            };
            if (stale)
            {
                _ = databases.RequestCatchUp(databaseId);
                return;
            }
        }
    }

    private static LogLevel AbortLevel(string reason) =>
        string.Equals(reason, WoundedReason, StringComparison.Ordinal) ? LogLevel.Debug : LogLevel.Warning;

    private static string AbortReason(ClusterCommitTransactionOutcome outcome) => outcome switch
    {
        CommitMetadata => "committed",
        SerializationConflict conflict => conflict.Reason,
        SchemaMismatch => "schema_epoch_stale",
        ClusterUnavailable unavailable => unavailable.Code,
        ClusterTimeout timeout => timeout.Code,
        StorageBusy => "storage_busy",
    };

    private static ClusterWriteOutcome Written(
        StagedWrite staged,
        ClusterCommitTransactionOutcome committed) => committed switch
        {
            CommitMetadata commit => new SqlWriteResult(staged.ResultSets, commit),
            SerializationConflict conflict => conflict,
            SchemaMismatch mismatch => mismatch,
            ClusterUnavailable unavailable => unavailable,
            ClusterTimeout timeout => timeout,
            StorageBusy busy => busy,
        };

    private static SerializationConflict Conflict(StagedWrite staged, string reason) => new(staged.DatabaseId, staged.TxId, reason);

    private static ClusterCommitTransactionOutcome Rejection(Ulid databaseId, StagedWrite staged, StrongVoteTally tally, StrongTransactionHandle handle)
    {
        var rejections = tally.Rejections;
        var hasReadSetStale = false;
        var hasWounded = false;
        for (var index = 0; index < rejections.Count; index++)
        {
            var schema = rejections[index] switch
            {
                SchemaEpochStale stale => stale,
                ReadSetStale => Mark(ref hasReadSetStale),
                Wounded => Mark(ref hasWounded),
                Prepared => null,
            };
            if (schema is not null)
            {
                return new SchemaMismatch(databaseId, schema.ExpectedEpoch, schema.CurrentEpoch);
            }
        }

        if (hasReadSetStale)
        {
            return Conflict(staged, "read_set_stale");
        }

        if (hasWounded || handle.State == StrongTransactionState.Aborted)
        {
            return Conflict(staged, WoundedReason);
        }

        if (tally.Prepared >= tally.Required && !tally.WithinLease)
        {
            return new ClusterTimeout("strong quorum was reached after the lock lease budget");
        }

        return new ClusterUnavailable(
            string.Concat("strong quorum unreachable: ", tally.Prepared.ToString(CultureInfo.InvariantCulture), " of ", tally.Required.ToString(CultureInfo.InvariantCulture), " regions prepared"),
            "strong_quorum_unreachable");

        static SchemaEpochStale? Mark(ref bool flag)
        {
            flag = true;
            return null;
        }
    }

    private async ValueTask AbortAsync(ISqlDatabaseSession session, StagedWrite staged, StrongTransactionHandle handle, StrongPreparePayload payload, StrongVoteTally tally, string reason)
    {
        registry.Complete(handle, committed: false);
        var aborted = await session.AbortStagedAsync(staged.Token, CancellationToken.None);
        var released = aborted switch
        {
            StagedWriteAborted => true,
            StagedWriteMissing => false,
        };
        finalizer.Abort(session.DatabaseId, staged.TxId, payload.Priority, tally.RemoteParticipants);
        var level = AbortLevel(reason);
        ClusterLog.StrongAborted(logger, level, session.DatabaseId, staged.TxId, reason, tally.Prepared, tally.Required, released);
    }
}

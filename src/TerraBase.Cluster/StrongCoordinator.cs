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
    internal async ValueTask<StrongVoteTally> CollectAsync(ISqlDatabaseSession session, StrongPreparePayload payload, StrongTransactionHandle handle, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(payload);
        var started = timeProvider.GetTimestamp();
        var leaseDeadline = started + (long)(options.StrongLockLease.TotalSeconds * timeProvider.TimestampFrequency);
        var regionCount = topology.Regions.Count;
        var required = topology.RegionQuorum;
        var voteCancel = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var dispatchGate = new Lock();
        var closed = false;
        List<string> dispatched = [];
        List<Task<RegionVote>> votes = [.. topology.Regions.Select(region => VoteRegionAsync(session, payload, handle, region, dispatchGate, () => closed, dispatched, voteCancel.Token))];
        var prepared = 0;
        var unreachable = 0;
        List<StrongPrepareOutcome> rejections = [];
        try
        {
            await foreach (var completed in Task.WhenEach(votes).WithCancellation(cancellationToken))
            {
                var result = await completed;
                var definitive = result.Vote switch
                {
                    Prepared => Increment(ref prepared),
                    Wounded wounded => Reject(rejections, wounded, definitive: false),
                    ReadSetStale stale => Reject(rejections, stale, definitive: true),
                    SchemaEpochStale epoch => Reject(rejections, epoch, definitive: true),
                    ClusterUnavailable => Increment(ref unreachable),
                };
                if (prepared >= required || definitive || regionCount - rejections.Count - unreachable < required)
                {
                    await voteCancel.CancelAsync();
                    break;
                }
            }
        }
        finally
        {
            await voteCancel.CancelAsync();
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

        string[] remoteParticipants;
        lock (dispatchGate)
        {
            closed = true;
            remoteParticipants = [.. dispatched.Distinct(StringComparer.Ordinal)];
        }

        var withinLease = timeProvider.GetElapsedTime(started) < options.StrongLockLease / 2;
        return new StrongVoteTally(prepared, required, rejections, unreachable, remoteParticipants, leaseDeadline, withinLease);
    }

    private async Task<RegionVote> VoteRegionAsync(
        ISqlDatabaseSession session,
        StrongPreparePayload payload,
        StrongTransactionHandle handle,
        string region,
        Lock dispatchGate,
        Func<bool> isClosed,
        List<string> dispatched,
        CancellationToken cancellationToken)
    {
        var candidates = topology.StrongCandidates(region, payload.DatabaseId);
        StrongParticipantTarget[] ordered = [.. candidates.OrderByDescending(candidate => candidate.IsLocal || health.IsReachable(candidate.NodeId))];
        StrongVote last = new ClusterUnavailable("no candidate available in region", "region_unreachable");
        foreach (var candidate in ordered)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            if (!candidate.IsLocal)
            {
                lock (dispatchGate)
                {
                    if (isClosed() || cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }

                    handle.DispatchTo(candidate.NodeId);
                    dispatched.Add(candidate.NodeId);
                }
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

    private static bool IsRetryable(StrongVote vote) => vote switch
    {
        ClusterUnavailable => true,
        Prepared or Wounded or ReadSetStale or SchemaEpochStale => false,
    };

    private sealed record RegionVote(StrongVote Vote);

    private static bool Increment(ref int counter)
    {
        counter++;
        return false;
    }

    private static bool Reject(List<StrongPrepareOutcome> rejections, StrongPrepareOutcome outcome, bool definitive)
    {
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

    private async Task<StrongVote> VoteAsync(ISqlDatabaseSession session, StrongPreparePayload payload, StrongParticipantTarget target, CancellationToken cancellationToken)
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

            using var timer = new CancellationTokenSource();
            var elapsed = Task.Delay(timeProvider.GetElapsedTime(now, tally.LeaseDeadline), timeProvider, timer.Token);
            var completion = session.CommitStagedAsync(staged.Token, clock.Tick(), tally.LeaseDeadline, CancellationToken.None).AsTask();
            if (await Task.WhenAny(completion, elapsed) != completion && !completion.IsCompletedSuccessfully)
            {
                recovery.Enqueue(() => ResolveExpiredCommitAsync(session, staged, handle, payload, tally, completion));
                deferred = true;
                return new ClusterUnavailable("the staged commit's outcome could not be confirmed before its participant leases expired", "write_outcome_unknown");
            }

            await timer.CancelAsync();
            committed = await completion;
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
        if (tally.Rejections.Any(static rejection => rejection switch
        {
            ReadSetStale or SchemaEpochStale => true,
            Prepared or Wounded => false,
        }))
        {
            _ = databases.RequestCatchUp(databaseId);
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
        foreach (var rejection in tally.Rejections)
        {
            var schema = rejection switch
            {
                SchemaEpochStale stale => stale,
                Prepared or Wounded or ReadSetStale => null,
            };
            if (schema is not null)
            {
                return new SchemaMismatch(databaseId, schema.ExpectedEpoch, schema.CurrentEpoch);
            }
        }

        if (tally.Rejections.Any(static rejection => rejection switch
        {
            ReadSetStale => true,
            Prepared or Wounded or SchemaEpochStale => false,
        }))
        {
            return Conflict(staged, "read_set_stale");
        }

        if (tally.Rejections.Any(static rejection => rejection switch
        {
            Wounded => true,
            Prepared or ReadSetStale or SchemaEpochStale => false,
        }) || handle.State == StrongTransactionState.Aborted)
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

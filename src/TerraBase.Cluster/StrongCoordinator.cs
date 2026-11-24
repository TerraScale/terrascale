using System.Globalization;
using Microsoft.Extensions.Logging;
using TerraBase.Storage;
using TerraScale.Contracts.Quic;
using TerraScale.QuicRpc;

namespace TerraBase.Cluster;

public readonly union StrongVote(Prepared, Wounded, ReadSetStale, SchemaEpochStale, ClusterUnavailable);

public sealed record StrongVoteTally(int Prepared, int Required, IReadOnlyList<StrongPrepareOutcome> Rejections, int Unreachable, IReadOnlyList<string> RemoteParticipants, bool WithinLease);

public sealed class StrongVoteCollector(ClusterOptions options, ClusterTopology topology, PeerHealthMonitor health, StrongParticipant participant, QuicRpcClient client, TimeProvider timeProvider)
{
    public async ValueTask<StrongVoteTally> CollectAsync(ISqlDatabaseSession session, StrongPreparePayload payload, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(payload);
        var started = timeProvider.GetTimestamp();
        var regionCount = topology.Regions.Count;
        var required = topology.RegionQuorum;
        var voteCancel = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var dispatchGate = new Lock();
        var closed = false;
        List<string> dispatched = [];
        List<Task<RegionVote>> votes = [.. topology.Regions.Select(region => VoteRegionAsync(session, payload, region, dispatchGate, () => closed, dispatched, voteCancel.Token, cancellationToken))];
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
                    ((CancellationTokenSource)state!).Dispose();
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
        return new StrongVoteTally(prepared, required, rejections, unreachable, remoteParticipants, withinLease);
    }

    private async Task<RegionVote> VoteRegionAsync(
        ISqlDatabaseSession session,
        StrongPreparePayload payload,
        string region,
        Lock dispatchGate,
        Func<bool> isClosed,
        List<string> dispatched,
        CancellationToken voteToken,
        CancellationToken callerToken)
    {
        var candidates = topology.StrongCandidates(region, payload.DatabaseId);
        StrongParticipantTarget[] ordered = [.. candidates.OrderByDescending(candidate => candidate.IsLocal || health.IsReachable(candidate.NodeId))];
        StrongVote last = new ClusterUnavailable("no candidate available in region", "region_unreachable");
        foreach (var candidate in ordered)
        {
            if (voteToken.IsCancellationRequested)
            {
                break;
            }

            if (!candidate.IsLocal)
            {
                lock (dispatchGate)
                {
                    if (isClosed() || voteToken.IsCancellationRequested)
                    {
                        break;
                    }

                    dispatched.Add(candidate.NodeId);
                }
            }

            StrongVote vote;
            try
            {
                vote = await VoteAsync(session, payload, candidate, voteToken);
            }
            catch (OperationCanceledException) when (voteToken.IsCancellationRequested && !callerToken.IsCancellationRequested)
            {
                return new RegionVote(new ClusterUnavailable("vote canceled after quorum decision", "vote_canceled"));
            }

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

public sealed class StrongCoordinator(ClusterTopology topology, StrongVoteCollector votes, StrongTransactionRegistry registry, CommitFinalizer finalizer, HlcClock clock, ILogger<StrongCoordinator> logger)
{
    public async ValueTask<ClusterWriteOutcome> ExecuteAsync(ISqlDatabaseSession session, SqlStatementRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(request);
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
            var local = await session.CommitStagedAsync(staged.Token, clock.Tick(), cancellationToken);
            return local switch
            {
                CommitMetadata commit => commit,
                StagedWriteMissing => Conflict(staged, "staged_write_expired"),
                ReadSetConflict => Conflict(staged, "read_set_stale"),
                SchemaMismatch mismatch => mismatch,
                StorageBusy busy => busy,
            };
        }

        var handle = registry.Register(staged.TxId);
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
        var tally = await votes.CollectAsync(session, payload, cancellationToken);
        if (tally.Prepared < tally.Required || !tally.WithinLease || !handle.TryBeginCommit())
        {
            var reason = Rejection(session.DatabaseId, staged, tally, handle);
            await AbortAsync(session, staged, handle, payload, tally);
            return reason;
        }

        var committed = await session.CommitStagedAsync(staged.Token, clock.Tick(), cancellationToken);
        ClusterCommitTransactionOutcome outcome = committed switch
        {
            CommitMetadata commit => commit,
            StagedWriteMissing => Conflict(staged, "staged_write_expired"),
            ReadSetConflict => Conflict(staged, "read_set_stale"),
            SchemaMismatch mismatch => mismatch,
            StorageBusy busy => busy,
        };
        var finalized = committed switch
        {
            CommitMetadata commit => commit,
            StagedWriteMissing or ReadSetConflict or SchemaMismatch or StorageBusy => null,
        };
        if (finalized is null)
        {
            await AbortAsync(session, staged, handle, payload, tally);
            return outcome;
        }

        registry.Complete(handle, committed: true);
        await finalizer.FinalizeStrongAsync(session.DatabaseId, finalized, cancellationToken);
        return outcome;
    }

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
            return Conflict(staged, "wounded");
        }

        if (tally.Prepared >= tally.Required && !tally.WithinLease)
        {
            return new ClusterTimeout("strong quorum was reached after the lock lease budget");
        }

        return new ClusterUnavailable(
            string.Concat("strong quorum unreachable: ", tally.Prepared.ToString(CultureInfo.InvariantCulture), " of ", tally.Required.ToString(CultureInfo.InvariantCulture), " regions prepared"),
            "strong_quorum_unreachable");
    }

    private async ValueTask AbortAsync(ISqlDatabaseSession session, StagedWrite staged, StrongTransactionHandle handle, StrongPreparePayload payload, StrongVoteTally tally)
    {
        registry.Complete(handle, committed: false);
        var aborted = await session.AbortStagedAsync(staged.Token, CancellationToken.None);
        var released = aborted switch
        {
            StagedWriteAborted => true,
            StagedWriteMissing => false,
        };
        finalizer.Abort(session.DatabaseId, staged.TxId, payload.Priority, tally.RemoteParticipants);
        ClusterLog.StrongAborted(logger, session.DatabaseId, staged.TxId, tally.Prepared, tally.Required, released);
    }
}

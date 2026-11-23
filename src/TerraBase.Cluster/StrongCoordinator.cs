using System.Globalization;
using Microsoft.Extensions.Logging;
using TerraBase.Storage;
using TerraScale.Contracts.Quic;
using TerraScale.QuicRpc;

namespace TerraBase.Cluster;

public readonly union StrongVote(Prepared, Wounded, ReadSetStale, SchemaEpochStale, Unavailable);

public sealed record StrongVoteTally(int Prepared, int Required, IReadOnlyList<StrongPrepareOutcome> Rejections, int Unreachable, IReadOnlyList<string> RemoteParticipants, bool WithinLease);

public sealed class StrongVoteCollector(ClusterOptions options, ClusterTopology topology, StrongParticipant participant, QuicRpcClient client, TimeProvider timeProvider)
{
    public async ValueTask<StrongVoteTally> CollectAsync(ISqlDatabaseSession session, StrongPreparePayload payload, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(payload);
        var started = timeProvider.GetTimestamp();
        var targets = topology.StrongParticipants(payload.DatabaseId);
        var required = topology.RegionQuorum;
        List<Task<StrongVote>> votes = [.. targets.Select(target => VoteAsync(session, payload, target, cancellationToken))];
        var prepared = 0;
        var unreachable = 0;
        List<StrongPrepareOutcome> rejections = [];
        await foreach (var completed in Task.WhenEach(votes).WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            var vote = await completed.ConfigureAwait(false);
            var definitive = vote switch
            {
                Prepared => Increment(ref prepared),
                Wounded wounded => Reject(rejections, wounded, definitive: false),
                ReadSetStale stale => Reject(rejections, stale, definitive: true),
                SchemaEpochStale epoch => Reject(rejections, epoch, definitive: true),
                Unavailable => Increment(ref unreachable),
            };
            if (prepared >= required || definitive || targets.Count - rejections.Count - unreachable < required)
            {
                break;
            }
        }

        var withinLease = timeProvider.GetElapsedTime(started) < options.StrongLockLease / 2;
        return new StrongVoteTally(prepared, required, rejections, unreachable, [.. targets.Where(static target => !target.IsLocal).Select(static target => target.NodeId)], withinLease);
    }

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
            return await participant.PrepareAsync(session, payload, options.NodeId, CausalBookmark.Empty, cancellationToken).ConfigureAwait(false);
        }

        var response = await client.CallAsync<StrongPrepareMessage, StrongPrepareResponseMessage>(
            target.NodeId,
            QuicMessageType.StrongPrepare,
            new StrongPrepareMessage(payload, options.NodeId, session.CurrentBookmark),
            options.StrongPrepareTimeout,
            cancellationToken).ConfigureAwait(false);
        return response switch
        {
            StrongPrepareResponseMessage message => Flatten(message.Outcome),
            Unavailable unavailable => unavailable,
            RequestTimeout timeout => new Unavailable(timeout.Message, timeout.Code),
            QuicRemoteError error => new Unavailable(error.Message, error.Code),
        };
    }
}

public sealed class StrongCoordinator(ClusterTopology topology, StrongVoteCollector votes, StrongTransactionRegistry registry, CommitFinalizer finalizer, HlcClock clock, ILogger<StrongCoordinator> logger)
{
    public async ValueTask<Result<SqlWriteResult, SqlError, SerializationConflict, SchemaMismatch, Unavailable, RequestTimeout, StorageBusy>> ExecuteAsync(ISqlDatabaseSession session, SqlStatementRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(request);
        var staged = await session.StageWriteAsync(request with { Mode = ConsistencyMode.Strong }, cancellationToken).ConfigureAwait(false);
        return staged switch
        {
            StagedWrite write => Written(write, await CommitAsync(session, write, cancellationToken).ConfigureAwait(false)),
            SqlError error => error,
            SchemaMismatch mismatch => mismatch,
            StorageBusy busy => busy,
            RequestTimeout timeout => timeout,
        };
    }

    public async ValueTask<Result<CommitMetadata, SerializationConflict, SchemaMismatch, Unavailable, RequestTimeout, StorageBusy>> CommitTransactionAsync(ISqlDatabaseSession session, ISqlTransaction transaction, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(transaction);
        var staged = await transaction.StageAsync(cancellationToken).ConfigureAwait(false);
        return staged switch
        {
            StagedWrite write => await CommitAsync(session, write, cancellationToken).ConfigureAwait(false),
            SchemaMismatch mismatch => mismatch,
            StorageBusy busy => busy,
            RequestTimeout timeout => timeout,
        };
    }

    public async ValueTask<Result<CommitMetadata, SerializationConflict, SchemaMismatch, Unavailable, RequestTimeout, StorageBusy>> CommitAsync(ISqlDatabaseSession session, StagedWrite staged, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(staged);
        if (staged.WriteKeys.Count == 0 && !staged.IsSchemaChange && staged.DeltaPayload.IsEmpty)
        {
            var local = await session.CommitStagedAsync(staged.Token, clock.Tick(), cancellationToken).ConfigureAwait(false);
            return local switch
            {
                CommitMetadata commit => commit,
                NotFound => Conflict(staged, "staged_write_expired"),
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
        var tally = await votes.CollectAsync(session, payload, cancellationToken).ConfigureAwait(false);
        if (tally.Prepared < tally.Required || !tally.WithinLease || !handle.TryBeginCommit())
        {
            var reason = Rejection(session.DatabaseId, staged, tally, handle);
            await AbortAsync(session, staged, handle, payload, tally).ConfigureAwait(false);
            return reason;
        }

        var committed = await session.CommitStagedAsync(staged.Token, clock.Tick(), cancellationToken).ConfigureAwait(false);
        Result<CommitMetadata, SerializationConflict, SchemaMismatch, Unavailable, RequestTimeout, StorageBusy> outcome = committed switch
        {
            CommitMetadata commit => commit,
            NotFound => Conflict(staged, "staged_write_expired"),
            ReadSetConflict => Conflict(staged, "read_set_stale"),
            SchemaMismatch mismatch => mismatch,
            StorageBusy busy => busy,
        };
        var finalized = committed switch
        {
            CommitMetadata commit => commit,
            NotFound or ReadSetConflict or SchemaMismatch or StorageBusy => null,
        };
        if (finalized is null)
        {
            await AbortAsync(session, staged, handle, payload, tally).ConfigureAwait(false);
            return outcome;
        }

        registry.Complete(handle, committed: true);
        await finalizer.FinalizeStrongAsync(session.DatabaseId, finalized, cancellationToken).ConfigureAwait(false);
        return outcome;
    }

    private static Result<SqlWriteResult, SqlError, SerializationConflict, SchemaMismatch, Unavailable, RequestTimeout, StorageBusy> Written(
        StagedWrite staged,
        Result<CommitMetadata, SerializationConflict, SchemaMismatch, Unavailable, RequestTimeout, StorageBusy> committed) => committed switch
        {
            CommitMetadata commit => new SqlWriteResult(staged.ResultSets, commit),
            SerializationConflict conflict => conflict,
            SchemaMismatch mismatch => mismatch,
            Unavailable unavailable => unavailable,
            RequestTimeout timeout => timeout,
            StorageBusy busy => busy,
        };

    private static SerializationConflict Conflict(StagedWrite staged, string reason) => new(staged.DatabaseId, staged.TxId, reason);

    private static Result<CommitMetadata, SerializationConflict, SchemaMismatch, Unavailable, RequestTimeout, StorageBusy> Rejection(Ulid databaseId, StagedWrite staged, StrongVoteTally tally, StrongTransactionHandle handle)
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

        if (!tally.WithinLease)
        {
            return new RequestTimeout("strong quorum was reached after the lock lease budget");
        }

        return new Unavailable(
            string.Concat("strong quorum unreachable: ", tally.Prepared.ToString(CultureInfo.InvariantCulture), " of ", tally.Required.ToString(CultureInfo.InvariantCulture), " regions prepared"),
            "strong_quorum_unreachable");
    }

    private async ValueTask AbortAsync(ISqlDatabaseSession session, StagedWrite staged, StrongTransactionHandle handle, StrongPreparePayload payload, StrongVoteTally tally)
    {
        registry.Complete(handle, committed: false);
        var aborted = await session.AbortStagedAsync(staged.Token, CancellationToken.None).ConfigureAwait(false);
        var released = aborted switch
        {
            Done => true,
            NotFound => false,
        };
        finalizer.Abort(session.DatabaseId, staged.TxId, payload.Priority, tally.RemoteParticipants);
        ClusterLog.StrongAborted(logger, session.DatabaseId, staged.TxId, tally.Prepared, tally.Required, released);
    }
}

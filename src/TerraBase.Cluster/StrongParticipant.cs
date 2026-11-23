using System.Diagnostics;
using Microsoft.Extensions.Logging;
using TerraBase.Storage;
using TerraScale.Contracts.Quic;
using TerraScale.QuicRpc;

namespace TerraBase.Cluster;

public sealed class StrongParticipant(ClusterOptions options, ClusterDatabases databases, StrongLockTable locks, StrongTransactionRegistry registry, QuicRpcClient client, HlcClock clock, ILogger<StrongParticipant> logger)
{
    private const int MaxWoundRounds = 4;

    public void Map(QuicRpcHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        _ = host.MapUnary<StrongPrepareMessage, StrongPrepareResponseMessage>(QuicMessageType.StrongPrepare, HandlePrepareAsync);
        _ = host.MapUnary<StrongWoundMessage, StrongWoundResponseMessage>(QuicMessageType.StrongWound, HandleWoundAsync);
    }

    public async ValueTask<StrongVote> PrepareAsync(ISqlDatabaseSession session, StrongPreparePayload payload, string coordinatorNodeId, CausalBookmark originBookmark, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(originBookmark);
        if (!session.CurrentBookmark.Dominates(originBookmark))
        {
            _ = databases.RequestCatchUp(payload.DatabaseId);
            var caughtUp = await session.WaitForBookmarkAsync(originBookmark, options.StrongPrepareTimeout / 2, cancellationToken).ConfigureAwait(false);
            var behind = caughtUp switch
            {
                Done => false,
                RequestTimeout => true,
            };
            if (behind)
            {
                return new Unavailable("participant has not applied the origin's causal history yet", "participant_behind");
            }
        }

        if (await ValidateAsync(session, payload, cancellationToken).ConfigureAwait(false) is { } invalid)
        {
            return invalid;
        }

        var holder = StrongLockHolder.From(payload, coordinatorNodeId, locks.LeaseDeadline(options.StrongLockLease));
        for (var round = 0; round < MaxWoundRounds; round++)
        {
            var attempt = locks.TryAcquire(holder);
            var decision = attempt switch
            {
                LockGranted => await GrantedAsync(session, payload, cancellationToken).ConfigureAwait(false),
                LockDenied denied => Denied(payload, denied.Winner),
                LockWoundRequired wound => await WoundAllAsync(payload, wound.Victims, cancellationToken).ConfigureAwait(false),
            };
            if (decision is { } decided)
            {
                return decided;
            }
        }

        return Denied(payload, holder);
    }

    private async ValueTask<StrongVote?> GrantedAsync(ISqlDatabaseSession session, StrongPreparePayload payload, CancellationToken cancellationToken)
    {
        var revalidated = await ValidateAsync(session, payload, cancellationToken).ConfigureAwait(false);
        if (revalidated is not null)
        {
            _ = locks.Release(payload.DatabaseId, payload.TxId);
            return revalidated;
        }

        return new Prepared(payload.TxId, options.Region, clock.Observe(payload.Priority));
    }

    private static StrongVote Denied(StrongPreparePayload payload, StrongLockHolder winner) =>
        new Wounded(payload.TxId, winner.TxId, winner.Priority);

    private async ValueTask<StrongVote?> WoundAllAsync(StrongPreparePayload payload, IReadOnlyList<StrongLockHolder> victims, CancellationToken cancellationToken)
    {
        foreach (var victim in victims)
        {
            var decision = await WoundAsync(victim, payload, cancellationToken).ConfigureAwait(false);
            var cleared = decision switch
            {
                WoundDecision.Aborted => Wound(victim, payload),
                WoundDecision.Committed or WoundDecision.Committing => await locks.WaitReleasedAsync(victim.TxId, options.StrongPrepareTimeout / 2, cancellationToken).ConfigureAwait(false),
                null => false,
                _ => throw new UnreachableException(),
            };
            if (!cleared)
            {
                return Denied(payload, victim);
            }
        }

        return null;
    }

    private bool Wound(StrongLockHolder victim, StrongPreparePayload winner)
    {
        locks.Wound(victim);
        ClusterLog.Wounded(logger, victim.TxId, winner.TxId);
        return true;
    }

    private async ValueTask<WoundDecision?> WoundAsync(StrongLockHolder victim, StrongPreparePayload winner, CancellationToken cancellationToken)
    {
        if (string.Equals(victim.CoordinatorNodeId, options.NodeId, StringComparison.Ordinal))
        {
            return registry.TryWound(victim.TxId);
        }

        var message = new StrongWoundMessage(new StrongWoundPayload(victim.DatabaseId, victim.TxId, winner.TxId, winner.Priority));
        var response = await client.CallAsync<StrongWoundMessage, StrongWoundResponseMessage>(victim.CoordinatorNodeId, QuicMessageType.StrongWound, message, options.WoundTimeout, cancellationToken).ConfigureAwait(false);
        return response switch
        {
            StrongWoundResponseMessage answer => answer.Decision,
            Unavailable or RequestTimeout or QuicRemoteError => null,
        };
    }

    private static async ValueTask<StrongVote?> ValidateAsync(ISqlDatabaseSession session, StrongPreparePayload payload, CancellationToken cancellationToken)
    {
        var validated = await session.ValidateReadSetAsync(payload.ReadSnapshot, payload.ReadSet, payload.ScannedTables, payload.SchemaEpoch, cancellationToken).ConfigureAwait(false);
        return validated switch
        {
            Done => null,
            ReadSetConflict conflict => Vote(new ReadSetStale(payload.TxId, conflict.StaleKeys, conflict.StaleTables)),
            SchemaMismatch mismatch => Vote(new SchemaEpochStale(payload.TxId, mismatch.ExpectedEpoch, mismatch.ActualEpoch)),
            StorageBusy busy => Vote(new Unavailable(busy.Message, "storage_busy")),
        };
    }

    private async ValueTask<Result<StrongPrepareResponseMessage, QuicRemoteError>> HandlePrepareAsync(QuicCallContext context, StrongPrepareMessage message, CancellationToken cancellationToken)
    {
        var resolved = await databases.ResolveAsync(message.Payload.DatabaseId, cancellationToken).ConfigureAwait(false);
        return resolved switch
        {
            ISqlDatabaseSession session => await PrepareLeasedAsync(session, message, cancellationToken).ConfigureAwait(false),
            NotFound notFound => new QuicRemoteError("database_not_found", notFound.Message ?? "database not found"),
            Unavailable unavailable => new QuicRemoteError(unavailable.Code, unavailable.Message),
        };
    }

    private async ValueTask<Result<StrongPrepareResponseMessage, QuicRemoteError>> PrepareLeasedAsync(ISqlDatabaseSession session, StrongPrepareMessage message, CancellationToken cancellationToken)
    {
        await using (session.ConfigureAwait(false))
        {
            var vote = await PrepareAsync(session, message.Payload, message.CoordinatorNodeId, message.OriginBookmark, cancellationToken).ConfigureAwait(false);
            return vote switch
            {
                Prepared prepared => Respond(prepared),
                Wounded wounded => Respond(wounded),
                ReadSetStale stale => Respond(stale),
                SchemaEpochStale epoch => Respond(epoch),
                Unavailable unavailable => new QuicRemoteError(unavailable.Code, unavailable.Message),
            };
        }
    }

    private static StrongVote? Vote(StrongVote vote) => vote;

    private static Result<StrongPrepareResponseMessage, QuicRemoteError> Respond(StrongPrepareOutcome outcome) => new StrongPrepareResponseMessage(outcome);

    private ValueTask<Result<StrongWoundResponseMessage, QuicRemoteError>> HandleWoundAsync(QuicCallContext context, StrongWoundMessage message, CancellationToken cancellationToken)
    {
        var decision = registry.TryWound(message.Payload.TxId);
        return ValueTask.FromResult<Result<StrongWoundResponseMessage, QuicRemoteError>>(new StrongWoundResponseMessage(decision));
    }
}

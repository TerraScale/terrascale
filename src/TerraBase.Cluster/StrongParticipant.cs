using System.Diagnostics;
using Microsoft.Extensions.Logging;
using TerraBase.Storage;
using TerraScale.Contracts.Quic;
using TerraScale.QuicRpc;

namespace TerraBase.Cluster;

public sealed class StrongParticipant(ClusterOptions options, ClusterTopology topology, ClusterDatabases databases, StrongLockTable locks, StrongTransactionRegistry registry, DeltaReplicator replicator, AntiEntropyService antiEntropy, QuicRpcClient client, HlcClock clock, TimeProvider timeProvider, ILogger<StrongParticipant> logger)
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
        var isRemoteCoordinator = !string.Equals(coordinatorNodeId, options.NodeId, StringComparison.Ordinal);
        if (!session.CurrentBookmark.Dominates(originBookmark))
        {
            var budget = options.StrongPrepareTimeout / 2;
            var started = timeProvider.GetTimestamp();
            await replicator.FlushInboundAsync(payload.DatabaseId, budget, cancellationToken);
            if (!session.CurrentBookmark.Dominates(originBookmark))
            {
                var remaining = budget - timeProvider.GetElapsedTime(started);
                var behind = remaining <= TimeSpan.Zero || (isRemoteCoordinator
                    ? !await antiEntropy.PullUntilAsync(session, coordinatorNodeId, originBookmark, remaining, cancellationToken)
                    : await WaitLocalCatchUpAsync(session, payload.DatabaseId, originBookmark, remaining, cancellationToken));
                if (behind)
                {
                    _ = databases.RequestCatchUp(payload.DatabaseId, originBookmark);
                    return new ClusterUnavailable("participant has not applied the origin's causal history yet", "participant_behind");
                }
            }
        }

        var seen = isRemoteCoordinator ? originBookmark : null;
        if (await ValidateAsync(session, payload, seen, cancellationToken) is { } invalid)
        {
            return invalid;
        }

        var holder = StrongLockHolder.From(payload, coordinatorNodeId, locks.LeaseDeadline(options.StrongLockLease));
        for (var round = 0; round < MaxWoundRounds; round++)
        {
            var attempt = locks.TryAcquire(holder);
            var decision = attempt switch
            {
                LockGranted => await GrantedAsync(session, payload, seen, cancellationToken),
                LockDenied denied => Denied(payload, denied.Winner),
                LockWoundRequired wound => await WoundAllAsync(payload, wound.Victims, cancellationToken),
            };
            if (decision is { } decided)
            {
                return decided;
            }
        }

        return Denied(payload, holder);
    }

    private async ValueTask<bool> WaitLocalCatchUpAsync(ISqlDatabaseSession session, Ulid databaseId, CausalBookmark originBookmark, TimeSpan timeout, CancellationToken cancellationToken)
    {
        _ = databases.RequestCatchUp(databaseId, originBookmark);
        var caughtUp = await session.WaitForBookmarkAsync(originBookmark, timeout, cancellationToken);
        return caughtUp switch
        {
            BookmarkReached => false,
            StorageTimeout => true,
        };
    }

    private async ValueTask<StrongVote?> GrantedAsync(ISqlDatabaseSession session, StrongPreparePayload payload, CausalBookmark? seen, CancellationToken cancellationToken)
    {
        var revalidated = await ValidateAsync(session, payload, seen, cancellationToken);
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
            var decision = await WoundAsync(victim, payload, cancellationToken);
            var cleared = decision switch
            {
                WoundDecision.Aborted => Wound(victim, payload),
                WoundDecision.Committed or WoundDecision.Committing => await locks.WaitReleasedAsync(victim.TxId, options.StrongPrepareTimeout / 2, cancellationToken),
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
        var response = await client.CallAsync<StrongWoundMessage, StrongWoundResponseMessage>(victim.CoordinatorNodeId, QuicMessageType.StrongWound, message, options.WoundTimeout, cancellationToken);
        return response switch
        {
            StrongWoundResponseMessage answer => answer.Decision,
            QuicUnavailable or QuicTimeout or QuicRemoteError => null,
        };
    }

    private static async ValueTask<StrongVote?> ValidateAsync(ISqlDatabaseSession session, StrongPreparePayload payload, CausalBookmark? seen, CancellationToken cancellationToken)
    {
        var covered = Covered(session, seen);
        var validated = await ValidateReadSetAsync(session, payload, covered, cancellationToken);
        if (covered && !Covered(session, seen))
        {
            validated = await ValidateReadSetAsync(session, payload, covered: false, cancellationToken);
        }

        return validated switch
        {
            ReadSetValid => null,
            ReadSetConflict conflict => Vote(new ReadSetStale(payload.TxId, conflict.StaleKeys, conflict.StaleTables)),
            SchemaMismatch mismatch => Vote(new SchemaEpochStale(payload.TxId, mismatch.ExpectedEpoch, mismatch.ActualEpoch)),
            StorageBusy busy => Vote(new ClusterUnavailable(busy.Message, "storage_busy")),
        };
    }

    private static bool Covered(ISqlDatabaseSession session, CausalBookmark? seen) => seen is { } origin && origin.Dominates(session.CurrentBookmark);

    private static ValueTask<ValidateReadSetOutcome> ValidateReadSetAsync(ISqlDatabaseSession session, StrongPreparePayload payload, bool covered, CancellationToken cancellationToken) =>
        session.ValidateReadSetAsync(payload.ReadSnapshot, payload.ReadSet, covered ? [] : payload.ScannedTables, payload.SchemaEpoch, cancellationToken);

    private async ValueTask<QuicHandlerOutcome<StrongPrepareResponseMessage>> HandlePrepareAsync(QuicCallContext context, StrongPrepareMessage message, CancellationToken cancellationToken)
    {
        if (!topology.IsPeer(context, message.CoordinatorNodeId)
            || !string.Equals(context.RemoteRegion, message.Payload.OriginRegion, StringComparison.Ordinal))
        {
            return ClusterTopology.PeerForbidden();
        }

        var resolved = await databases.ResolveAsync(message.Payload.DatabaseId, cancellationToken);
        return resolved switch
        {
            ISqlDatabaseSession session => await PrepareLeasedAsync(session, message, context.RemoteNodeId, cancellationToken),
            DatabaseNotFound notFound => new QuicRemoteError("database_not_found", notFound.Message),
            ClusterUnavailable unavailable => new QuicRemoteError(unavailable.Code, unavailable.Message),
        };
    }

    private async ValueTask<QuicHandlerOutcome<StrongPrepareResponseMessage>> PrepareLeasedAsync(ISqlDatabaseSession session, StrongPrepareMessage message, string coordinatorNodeId, CancellationToken cancellationToken)
    {
        await using (session)
        {
            var vote = await PrepareAsync(session, message.Payload, coordinatorNodeId, message.OriginBookmark, cancellationToken);
            return vote switch
            {
                Prepared prepared => Respond(prepared),
                Wounded wounded => Respond(wounded),
                ReadSetStale stale => Respond(stale),
                SchemaEpochStale epoch => Respond(epoch),
                ClusterUnavailable unavailable => new QuicRemoteError(unavailable.Code, unavailable.Message),
            };
        }
    }

    private static StrongVote? Vote(StrongVote vote) => vote;

    private static QuicHandlerOutcome<StrongPrepareResponseMessage> Respond(StrongPrepareOutcome outcome) => new StrongPrepareResponseMessage(outcome);

    private ValueTask<QuicHandlerOutcome<StrongWoundResponseMessage>> HandleWoundAsync(QuicCallContext context, StrongWoundMessage message, CancellationToken cancellationToken)
    {
        var payload = message.Payload;
        QuicHandlerOutcome<StrongWoundResponseMessage> outcome = topology.IsPeer(context)
            && registry.TryWoundFrom(payload, context.RemoteNodeId) is { } decision
                ? new StrongWoundResponseMessage(decision)
                : ClusterTopology.PeerForbidden();
        return ValueTask.FromResult(outcome);
    }
}

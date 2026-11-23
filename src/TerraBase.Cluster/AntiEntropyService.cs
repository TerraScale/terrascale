using System.Globalization;
using Microsoft.Extensions.Logging;
using TerraScale.Contracts.Quic;
using TerraScale.QuicRpc;

namespace TerraBase.Cluster;

public sealed record CatchUpReport(Ulid DatabaseId, int Fetched, int Applied, int PeersContacted, IReadOnlyList<string> Failures)
{
    public static CatchUpReport Empty(Ulid databaseId) => new(databaseId, 0, 0, 0, []);
}

public sealed class AntiEntropyService(ClusterOptions options, ClusterDatabases databases, DeltaReplicator replicator, QuicRpcClient client, PeerHealthMonitor health, TimeProvider timeProvider, ILogger<AntiEntropyService> logger)
{
    private const int MaxPagesPerPeer = 64;

    public void Map(QuicRpcHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        _ = host.MapUnary<CatchUpRequestMessage, CatchUpResponseMessage>(QuicMessageType.CatchUpRequest, HandleCatchUpAsync);
    }

    public async Task RunPeriodicAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(options.AntiEntropyInterval, timeProvider);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                foreach (var databaseId in databases.Tracked)
                {
                    _ = await SyncAsync(databaseId, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
    }

    public async Task RunOnDemandAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var databaseId in databases.CatchUpRequests.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                HashSet<Ulid> batch = [databaseId];
                while (databases.CatchUpRequests.TryRead(out var next))
                {
                    _ = batch.Add(next);
                }

                foreach (var pending in batch)
                {
                    _ = await SyncAsync(pending, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
    }

    public ValueTask<CatchUpReport> SyncAsync(Ulid databaseId, CancellationToken cancellationToken) =>
        SyncAsync(databaseId, options.CatchUpTimeout, cancellationToken);

    public async ValueTask<CatchUpReport> SyncAsync(Ulid databaseId, TimeSpan perPeerTimeout, CancellationToken cancellationToken)
    {
        var resolved = await databases.ResolveAsync(databaseId, cancellationToken).ConfigureAwait(false);
        return resolved switch
        {
            ISqlDatabaseSession session => await SyncLeasedAsync(session, perPeerTimeout, cancellationToken).ConfigureAwait(false),
            NotFound notFound => new CatchUpReport(databaseId, 0, 0, 0, [notFound.Message ?? "database not found"]),
            Unavailable unavailable => new CatchUpReport(databaseId, 0, 0, 0, [unavailable.Message]),
        };
    }

    public async ValueTask<bool> PullUntilAsync(ISqlDatabaseSession session, string nodeId, CausalBookmark target, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeId);
        ArgumentNullException.ThrowIfNull(target);
        var started = timeProvider.GetTimestamp();
        var retried = false;
        for (var page = 0; page < MaxPagesPerPeer && !session.CurrentBookmark.Dominates(target); page++)
        {
            var remaining = timeout - timeProvider.GetElapsedTime(started);
            if (remaining <= TimeSpan.Zero)
            {
                break;
            }

            var (_, applied, more, failure, transportReset) = await PullAsync(session, nodeId, remaining, cancellationToken).ConfigureAwait(false);
            if (session.CurrentBookmark.Dominates(target))
            {
                return true;
            }

            if (transportReset && !retried)
            {
                retried = true;
                continue;
            }

            if (!more || failure is not null || applied == 0)
            {
                break;
            }
        }

        return session.CurrentBookmark.Dominates(target);
    }

    private async ValueTask<CatchUpReport> SyncLeasedAsync(ISqlDatabaseSession session, TimeSpan perPeerTimeout, CancellationToken cancellationToken)
    {
        await using (session.ConfigureAwait(false))
        {
            PeerNodeInfo[] reachable = [.. options.Peers.Where(peer => health.IsReachable(peer.NodeId))];
            if (reachable.Length == 0)
            {
                return CatchUpReport.Empty(session.DatabaseId);
            }

            var results = new PeerSyncResult[reachable.Length];
            await Parallel.ForEachAsync(
                Enumerable.Range(0, reachable.Length),
                new ParallelOptions { MaxDegreeOfParallelism = options.MaxReplicationParallelism, CancellationToken = cancellationToken },
                async (index, token) => results[index] = await SyncPeerAsync(session, reachable[index].NodeId, perPeerTimeout, token).ConfigureAwait(false)).ConfigureAwait(false);

            var fetched = 0;
            var applied = 0;
            List<string> failures = [];
            foreach (var result in results)
            {
                fetched += result.Fetched;
                applied += result.Applied;
                failures.AddRange(result.Failures);
            }

            if (applied > 0)
            {
                ClusterLog.CatchUpApplied(logger, session.DatabaseId, applied, fetched, session.CurrentBookmark);
            }

            return new CatchUpReport(session.DatabaseId, fetched, applied, reachable.Length, failures);
        }
    }

    private async ValueTask<PeerSyncResult> SyncPeerAsync(ISqlDatabaseSession session, string nodeId, TimeSpan perPeerTimeout, CancellationToken cancellationToken)
    {
        var totalFetched = 0;
        var totalApplied = 0;
        List<string> failures = [];
        for (var page = 0; page < MaxPagesPerPeer; page++)
        {
            var (fetched, applied, more, failure, _) = await PullAsync(session, nodeId, perPeerTimeout, cancellationToken).ConfigureAwait(false);
            totalFetched += fetched;
            totalApplied += applied;
            if (failure is not null)
            {
                failures.Add(failure);
            }

            if (!more || failure is not null || applied == 0)
            {
                break;
            }
        }

        return new PeerSyncResult(totalFetched, totalApplied, failures);
    }

    private async ValueTask<(int Fetched, int Applied, bool More, string? Failure, bool TransportReset)> PullAsync(ISqlDatabaseSession session, string nodeId, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var request = new CatchUpRequestMessage(session.DatabaseId, options.NodeId, session.CurrentBookmark, options.CatchUpPageSize);
        var response = await client.CallAsync<CatchUpRequestMessage, CatchUpResponseMessage>(nodeId, QuicMessageType.CatchUpRequest, request, timeout, cancellationToken).ConfigureAwait(false);
        return response switch
        {
            CatchUpResponseMessage page => await ApplyPageAsync(session, page, cancellationToken).ConfigureAwait(false),
            Unavailable unavailable => (0, 0, false, string.Concat(nodeId, ": ", unavailable.Message), true),
            RequestTimeout timeoutError => TimedOutPull(nodeId, timeoutError),
            QuicRemoteError error => (0, 0, false, string.Concat(nodeId, ": ", error.Message), false),
        };
    }

    private (int Fetched, int Applied, bool More, string? Failure, bool TransportReset) TimedOutPull(string nodeId, RequestTimeout timeoutError)
    {
        client.ResetPeer(nodeId);
        return (0, 0, false, string.Concat(nodeId, ": ", timeoutError.Message), false);
    }

    private async ValueTask<(int Fetched, int Applied, bool More, string? Failure, bool TransportReset)> ApplyPageAsync(ISqlDatabaseSession session, CatchUpResponseMessage page, CancellationToken cancellationToken)
    {
        if (!page.Found || page.Entries.Count == 0)
        {
            return (0, 0, false, null, false);
        }

        var applied = await replicator.ApplyToSessionAsync(session, page.Entries, cancellationToken).ConfigureAwait(false);
        return applied switch
        {
            RemoteApplyOutcome outcome => (page.Entries.Count, outcome.Applied, page.HasMore, null, false),
            NotFound notFound => (page.Entries.Count, 0, false, notFound.Message ?? "database not found", false),
            SchemaMismatch mismatch => (page.Entries.Count, 0, false, string.Concat("schema epoch mismatch: expected ", mismatch.ExpectedEpoch.ToString(CultureInfo.InvariantCulture)), false),
            StorageCorrupted corrupted => (page.Entries.Count, 0, false, corrupted.Message, false),
            StorageBusy busy => (page.Entries.Count, 0, false, busy.Message, false),
            Unavailable unavailable => (page.Entries.Count, 0, false, unavailable.Message, false),
        };
    }

    private sealed record PeerSyncResult(int Fetched, int Applied, IReadOnlyList<string> Failures);

    private async ValueTask<Result<CatchUpResponseMessage, QuicRemoteError>> HandleCatchUpAsync(QuicCallContext context, CatchUpRequestMessage message, CancellationToken cancellationToken)
    {
        var resolved = await databases.ResolveAsync(message.DatabaseId, cancellationToken).ConfigureAwait(false);
        return resolved switch
        {
            ISqlDatabaseSession session => await ServeLeasedAsync(session, message, context.RemoteNodeId, cancellationToken).ConfigureAwait(false),
            NotFound => new CatchUpResponseMessage(message.DatabaseId, false, CausalBookmark.Empty, [], false),
            Unavailable unavailable => new QuicRemoteError(unavailable.Code, unavailable.Message),
        };
    }

    private async ValueTask<Result<CatchUpResponseMessage, QuicRemoteError>> ServeLeasedAsync(ISqlDatabaseSession session, CatchUpRequestMessage message, string requester, CancellationToken cancellationToken)
    {
        await using (session.ConfigureAwait(false))
        {
            var bookmark = session.CurrentBookmark;
            if (!bookmark.Dominates(message.Have))
            {
                _ = databases.RequestCatchUp(session.DatabaseId);
            }

            var budget = Math.Clamp(message.MaxEntries, 1, options.CatchUpPageSize);
            var lagging = bookmark.Entries
                .Where(entry => entry.Value > message.Have.Get(entry.Key))
                .ToArray();
            List<CommitMetadata> entries = [];
            var more = false;
            for (var index = 0; index < lagging.Length; index++)
            {
                var (origin, sequence) = lagging[index];
                var from = message.Have.Get(origin) + 1;
                var remainingBudget = budget - entries.Count;
                if (remainingBudget <= 0)
                {
                    more = true;
                    break;
                }

                var remainingOrigins = lagging.Length - index;
                var share = Math.Max(1, remainingBudget / remainingOrigins);
                var read = await session.ReadCommitLogAsync(origin, from, share, cancellationToken).ConfigureAwait(false);
                var page = read switch
                {
                    CommitLogPage served => served.Entries,
                    CommitLogGap or StorageBusy => [],
                };
                var failure = read switch
                {
                    CommitLogPage => null,
                    CommitLogGap gap => string.Concat("commit log gap for origin ", gap.OriginRegion, " from ", gap.RequestedFrom.ToString(CultureInfo.InvariantCulture)),
                    StorageBusy busy => busy.Message,
                };
                if (failure is not null)
                {
                    ClusterLog.CatchUpServeFailed(logger, session.DatabaseId, requester, failure);
                    continue;
                }

                entries.AddRange(page);
                more |= page.Count > 0 && page[^1].Sequence < sequence;
            }

            return new CatchUpResponseMessage(session.DatabaseId, true, bookmark, entries, more);
        }
    }
}

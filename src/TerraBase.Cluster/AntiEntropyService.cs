using System.Globalization;
using Microsoft.Extensions.Logging;
using TerraScale.Contracts.Continuity;
using TerraScale.Contracts.Quic;
using TerraScale.QuicRpc;

namespace TerraBase.Cluster;

public sealed record CatchUpReport(Ulid DatabaseId, int Fetched, int Applied, int PeersContacted, IReadOnlyList<string> Failures)
{
    public static CatchUpReport Empty(Ulid databaseId) => new(databaseId, 0, 0, 0, []);
}

public delegate ValueTask<ArchiveCatchUpOutcome> ArchiveCatchUp(ISqlDatabaseSession session, CausalBookmark target, CancellationToken cancellationToken);

public sealed record ArchiveCatchUpProgress(int Applied);

public readonly union ArchiveCatchUpOutcome(ArchiveCatchUpProgress, ClusterUnavailable);

public sealed class AntiEntropyService(ClusterOptions options, ClusterTopology topology, ClusterDatabases databases, ArchiveCatchUp recover, DeltaReplicator replicator, QuicRpcClient client, PeerHealthMonitor health, TimeProvider timeProvider, ILogger<AntiEntropyService> logger)
{
    private const int MaxPagesPerPeer = 64;
    private const string CommitLogGapCode = "commit_log_gap";

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
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                foreach (var databaseId in databases.Due())
                {
                    await SyncTrackedAsync(databaseId, onDemand: false, cancellationToken);
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
            await foreach (var databaseId in databases.CatchUpRequests.ReadAllAsync(cancellationToken))
            {
                await SyncTrackedAsync(databaseId, onDemand: true, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
    }

    private async Task SyncTrackedAsync(Ulid databaseId, bool onDemand, CancellationToken cancellationToken)
    {
        if (databases.BeginSync(databaseId, onDemand) is not { } tracked)
        {
            return;
        }

        var progressed = false;
        try
        {
            var report = await SyncAsync(databaseId, cancellationToken);
            progressed = report.Applied > 0;
        }
        finally
        {
            databases.EndSync(databaseId, tracked, progressed, requeue: !cancellationToken.IsCancellationRequested);
        }
    }

    public ValueTask<CatchUpReport> SyncAsync(Ulid databaseId, CancellationToken cancellationToken) =>
        SyncAsync(databaseId, options.CatchUpTimeout, cancellationToken);

    public ValueTask<CatchUpReport> SyncAsync(Ulid databaseId, TimeSpan perPeerTimeout, CancellationToken cancellationToken) =>
        SyncFromAsync(databaseId, options.Peers, perPeerTimeout, cancellationToken);

    public ValueTask<CatchUpReport> SyncInRegionAsync(Ulid databaseId, TimeSpan perPeerTimeout, CancellationToken cancellationToken)
    {
        var peers = options.RegionPeers;
        return peers.Count == 0 || !peers.Any(peer => health.IsReachable(peer.NodeId))
            ? ValueTask.FromResult(CatchUpReport.Empty(databaseId))
            : SyncFromAsync(databaseId, peers, perPeerTimeout, cancellationToken);
    }

    private async ValueTask<CatchUpReport> SyncFromAsync(Ulid databaseId, IReadOnlyList<PeerNodeInfo> peers, TimeSpan perPeerTimeout, CancellationToken cancellationToken)
    {
        if (peers.Count == 0)
        {
            return CatchUpReport.Empty(databaseId);
        }

        var resolved = await databases.ResolveAsync(databaseId, cancellationToken);
        return resolved switch
        {
            ISqlDatabaseSession session => await SyncLeasedAsync(session, peers, perPeerTimeout, cancellationToken),
            DatabaseNotFound notFound => new CatchUpReport(databaseId, 0, 0, 0, [notFound.Message]),
            ClusterUnavailable unavailable => new CatchUpReport(databaseId, 0, 0, 0, [unavailable.Message]),
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

            var (_, applied, more, failure, transportReset) = await PullAsync(session, nodeId, remaining, recoverFromArchive: false, cancellationToken);
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

    private async ValueTask<CatchUpReport> SyncLeasedAsync(ISqlDatabaseSession session, IReadOnlyList<PeerNodeInfo> peers, TimeSpan perPeerTimeout, CancellationToken cancellationToken)
    {
        await using (session)
        {
            PeerNodeInfo[] reachable = [.. peers.Where(peer => health.IsReachable(peer.NodeId))];
            if (reachable.Length == 0)
            {
                return CatchUpReport.Empty(session.DatabaseId);
            }

            var results = new PeerSyncResult[reachable.Length];
            await Parallel.ForEachAsync(
                Enumerable.Range(0, reachable.Length),
                new ParallelOptions { MaxDegreeOfParallelism = options.MaxReplicationParallelism, CancellationToken = cancellationToken },
                async (index, token) => results[index] = await SyncPeerAsync(session, reachable[index].NodeId, perPeerTimeout, token));

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
            var (fetched, applied, more, failure, _) = await PullAsync(session, nodeId, perPeerTimeout, recoverFromArchive: true, cancellationToken);
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

    private async ValueTask<(int Fetched, int Applied, bool More, string? Failure, bool TransportReset)> PullAsync(ISqlDatabaseSession session, string nodeId, TimeSpan timeout, bool recoverFromArchive, CancellationToken cancellationToken)
    {
        var request = new CatchUpRequestMessage(session.DatabaseId, options.NodeId, session.CurrentBookmark, options.CatchUpPageSize);
        var response = await client.CallAsync<CatchUpRequestMessage, CatchUpResponseMessage>(nodeId, QuicMessageType.CatchUpRequest, request, timeout, cancellationToken);
        return response switch
        {
            CatchUpResponseMessage page when page.DatabaseId == session.DatabaseId => await ApplyPageAsync(session, page, cancellationToken),
            CatchUpResponseMessage => (0, 0, false, "catch-up response named a different database", false),
            QuicUnavailable unavailable => (0, 0, false, string.Concat(nodeId, ": ", unavailable.Message), true),
            QuicTimeout timeoutError => TimedOutPull(nodeId, timeoutError),
            QuicRemoteError { Code: CommitLogGapCode } gap => await RecoverGapAsync(session, nodeId, gap, recoverFromArchive, cancellationToken),
            QuicRemoteError error => (0, 0, false, string.Concat(nodeId, ": ", error.Message), false),
        };
    }

    private async ValueTask<(int Fetched, int Applied, bool More, string? Failure, bool TransportReset)> RecoverGapAsync(
        ISqlDatabaseSession session, string nodeId, QuicRemoteError gap, bool recoverFromArchive, CancellationToken cancellationToken)
    {
        if (!CausalBookmark.TryDecode(gap.Message, out var target))
        {
            return (0, 0, false, string.Concat(nodeId, ": invalid catch-up gap bookmark"), false);
        }

        if (!recoverFromArchive)
        {
            _ = databases.RequestCatchUp(session.DatabaseId);
            return (0, 0, false, string.Concat(nodeId, ": commit log gap; archive catch-up queued"), false);
        }

        var recovered = await recover(session, target, cancellationToken);
        databases.Observe(session.DatabaseId, session.CurrentBookmark);
        return recovered switch
        {
            ArchiveCatchUpProgress progress => (progress.Applied, progress.Applied, true, null, false),
            ClusterUnavailable unavailable => (0, 0, false, string.Concat(nodeId, ": ", unavailable.Message), false),
        };
    }

    private (int Fetched, int Applied, bool More, string? Failure, bool TransportReset) TimedOutPull(string nodeId, QuicTimeout timeoutError)
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

        var applied = await replicator.ApplyToSessionAsync(session, page.Entries, requestCatchUpOnBuffered: false, cancellationToken);
        return applied switch
        {
            RemoteApplyOutcome outcome => (page.Entries.Count, outcome.Applied, page.HasMore, null, false),
            DatabaseNotFound notFound => (page.Entries.Count, 0, false, notFound.Message, false),
            SchemaMismatch mismatch => (page.Entries.Count, 0, false, string.Concat("schema epoch mismatch: expected ", mismatch.ExpectedEpoch.ToString(CultureInfo.InvariantCulture)), false),
            StorageCorrupted corrupted => (page.Entries.Count, 0, false, corrupted.Message, false),
            StorageBusy busy => (page.Entries.Count, 0, false, busy.Message, false),
            ClusterUnavailable unavailable => (page.Entries.Count, 0, false, unavailable.Message, false),
        };
    }

    private static QuicRemoteError Gap(CausalBookmark available, string origin, long sequence)
    {
        var bookmark = available.Encode();
        return new QuicRemoteError(CommitLogGapCode, bookmark.Length <= CausalBookmark.MaxEncodedLength
            ? bookmark
            : CausalBookmark.ForRegion(origin, sequence).Encode());
    }

    private sealed record PeerSyncResult(int Fetched, int Applied, IReadOnlyList<string> Failures);

    private async ValueTask<QuicHandlerOutcome<CatchUpResponseMessage>> HandleCatchUpAsync(QuicCallContext context, CatchUpRequestMessage message, CancellationToken cancellationToken)
    {
        if (!topology.IsPeer(context, message.RequesterNodeId))
        {
            return ClusterTopology.PeerForbidden();
        }

        var resolved = await databases.ResolveAsync(message.DatabaseId, cancellationToken);
        return resolved switch
        {
            ISqlDatabaseSession session => await ServeLeasedAsync(session, message, context.RemoteNodeId, cancellationToken),
            DatabaseNotFound => new CatchUpResponseMessage(message.DatabaseId, false, CausalBookmark.Empty, [], false),
            ClusterUnavailable unavailable => new QuicRemoteError(unavailable.Code, unavailable.Message),
        };
    }

    private async ValueTask<QuicHandlerOutcome<CatchUpResponseMessage>> ServeLeasedAsync(ISqlDatabaseSession session, CatchUpRequestMessage message, string requester, CancellationToken cancellationToken)
    {
        await using (session)
        {
            var bookmark = session.CurrentBookmark;
            if (!bookmark.Dominates(message.Have))
            {
                _ = databases.RequestCatchUp(session.DatabaseId, message.Have);
            }

            var budget = Math.Clamp(message.MaxEntries, 1, options.CatchUpPageSize);
            var lagging = bookmark.Entries
                .Where(entry => entry.Value > message.Have.Get(entry.Key))
                .ToArray();
            List<CommitMetadata> entries = [];
            var more = false;
            QuicRemoteError? refusal = null;
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
                var read = await session.ReadCommitLogAsync(origin, from, share, cancellationToken);
                var issue = read switch
                {
                    CommitLogPage { Entries.Count: > 0 } => null,
                    CommitLogPage or CommitLogGap => Gap(bookmark, origin, sequence),
                    StorageBusy busy => new QuicRemoteError("storage_busy", busy.Message),
                };
                if (issue is not null)
                {
                    ClusterLog.CatchUpServeFailed(logger, session.DatabaseId, requester, issue.Code);
                    if (refusal is null || issue.Code == CommitLogGapCode)
                    {
                        refusal = issue;
                    }

                    continue;
                }

                var page = read switch
                {
                    CommitLogPage served => served.Entries,
                    CommitLogGap or StorageBusy => [],
                };
                entries.AddRange(page);
                more |= page.Count > 0 && page[^1].Sequence < sequence;
            }

            return entries.Count == 0 && refusal is not null
                ? refusal
                : new CatchUpResponseMessage(session.DatabaseId, true, bookmark, entries, more || refusal is not null);
        }
    }
}

using System.Globalization;
using Microsoft.Extensions.Logging;
using TerraScale.Contracts.Continuity;
using TerraScale.Contracts.Quic;
using TerraScale.QuicRpc;

namespace TerraBase.Cluster;

public sealed record PeerCatchUp(string NodeId, bool Complete, bool HeadEmpty);

public sealed record CatchUpReport(Ulid DatabaseId, int Fetched, int Applied, int PeersContacted, IReadOnlyList<string> Failures, IReadOnlyList<PeerCatchUp> Peers)
{
    public static CatchUpReport Empty(Ulid databaseId) => new(databaseId, 0, 0, 0, [], []);
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
                    await SyncTrackedAsync(databaseId, databases.BeginSync(databaseId), cancellationToken);
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
            await foreach (var request in databases.CatchUpRequests.ReadAllAsync(cancellationToken))
            {
                await SyncTrackedAsync(request.DatabaseId, databases.BeginSync(request), cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
    }

    private async Task SyncTrackedAsync(Ulid databaseId, ClusterDatabases.TrackedDatabase? tracked, CancellationToken cancellationToken)
    {
        if (tracked is null)
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
            DatabaseNotFound notFound => new CatchUpReport(databaseId, 0, 0, 0, [notFound.Message], []),
            ClusterUnavailable unavailable => new CatchUpReport(databaseId, 0, 0, 0, [unavailable.Message], []),
        };
    }

    public async ValueTask<bool> PullUntilAsync(ISqlDatabaseSession session, string nodeId, CausalBookmark target, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeId);
        ArgumentNullException.ThrowIfNull(target);
        var started = timeProvider.GetTimestamp();
        var retried = false;
        var page = 0;
        while (page < MaxPagesPerPeer && !session.CurrentBookmark.Dominates(target))
        {
            var remaining = timeout - timeProvider.GetElapsedTime(started);
            if (remaining <= TimeSpan.Zero)
            {
                break;
            }

            var (_, applied, more, failure, transportReset, _) = await PullAsync(session, nodeId, remaining, recoverFromArchive: false, cancellationToken);
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

            page++;
        }

        return session.CurrentBookmark.Dominates(target);
    }

    public async ValueTask<CatchUpReport> SyncSessionAsync(ISqlDatabaseSession session, IReadOnlyList<PeerNodeInfo> peers, TimeSpan perPeerTimeout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(peers);
        return await SyncPeersAsync(session, peers, perPeerTimeout, cancellationToken);
    }

    private async ValueTask<CatchUpReport> SyncLeasedAsync(ISqlDatabaseSession session, IReadOnlyList<PeerNodeInfo> peers, TimeSpan perPeerTimeout, CancellationToken cancellationToken)
    {
        await using (session)
        {
            return await SyncPeersAsync(session, peers, perPeerTimeout, cancellationToken);
        }
    }

    private async ValueTask<CatchUpReport> SyncPeersAsync(ISqlDatabaseSession session, IReadOnlyList<PeerNodeInfo> peers, TimeSpan perPeerTimeout, CancellationToken cancellationToken)
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
        List<PeerCatchUp> outcomes = [];
        for (var index = 0; index < results.Length; index++)
        {
            var result = results[index];
            fetched += result.Fetched;
            applied += result.Applied;
            failures.AddRange(result.Failures);
            outcomes.Add(new PeerCatchUp(reachable[index].NodeId, result.Complete, result.HeadEmpty));
        }

        if (applied > 0)
        {
            ClusterLog.CatchUpApplied(logger, session.DatabaseId, applied, fetched, session.CurrentBookmark);
        }

        return new CatchUpReport(session.DatabaseId, fetched, applied, reachable.Length, failures, outcomes);
    }

    private async ValueTask<PeerSyncResult> SyncPeerAsync(ISqlDatabaseSession session, string nodeId, TimeSpan perPeerTimeout, CancellationToken cancellationToken)
    {
        var totalFetched = 0;
        var totalApplied = 0;
        var retried = false;
        var complete = false;
        var headEmpty = true;
        List<string> failures = [];
        var page = 0;
        while (page < MaxPagesPerPeer)
        {
            var (fetched, applied, more, failure, transportReset, head) = await PullAsync(session, nodeId, perPeerTimeout, recoverFromArchive: true, cancellationToken);
            totalFetched += fetched;
            totalApplied += applied;
            if (transportReset && !retried)
            {
                retried = true;
                continue;
            }

            if (failure is not null)
            {
                failures.Add(failure);
            }

            if (!more || failure is not null || applied == 0)
            {
                complete = !more && failure is null && head is not null && session.CurrentBookmark.Dominates(head);
                headEmpty = head is not { IsEmpty: false };
                break;
            }

            page++;
        }

        return new PeerSyncResult(totalFetched, totalApplied, failures, complete, headEmpty);
    }

    private async ValueTask<(int Fetched, int Applied, bool More, string? Failure, bool TransportReset, CausalBookmark? Head)> PullAsync(ISqlDatabaseSession session, string nodeId, TimeSpan timeout, bool recoverFromArchive, CancellationToken cancellationToken)
    {
        var request = new CatchUpRequestMessage(session.DatabaseId, options.NodeId, session.CurrentBookmark, options.CatchUpPageSize);
        var response = await client.CallAsync<CatchUpRequestMessage, CatchUpResponseMessage>(nodeId, QuicMessageType.CatchUpRequest, request, timeout, cancellationToken);
        return response switch
        {
            CatchUpResponseMessage page when page.DatabaseId == session.DatabaseId => await ApplyPageAsync(session, nodeId, page, cancellationToken),
            CatchUpResponseMessage => (0, 0, false, "catch-up response named a different database", false, null),
            QuicUnavailable unavailable => (0, 0, false, string.Concat(nodeId, ": ", unavailable.Message), IsTransient(unavailable), null),
            QuicTimeout timeoutError => TimedOutPull(nodeId, timeoutError),
            QuicRemoteError { Code: CommitLogGapCode } gap => await RecoverGapAsync(session, nodeId, gap, recoverFromArchive, cancellationToken),
            QuicRemoteError error => (0, 0, false, string.Concat(nodeId, ": ", error.Message), false, null),
        };
    }

    private async ValueTask<(int Fetched, int Applied, bool More, string? Failure, bool TransportReset, CausalBookmark? Head)> RecoverGapAsync(
        ISqlDatabaseSession session, string nodeId, QuicRemoteError gap, bool recoverFromArchive, CancellationToken cancellationToken)
    {
        if (!CausalBookmark.TryDecode(gap.Message, out var target))
        {
            return (0, 0, false, string.Concat(nodeId, ": invalid catch-up gap bookmark"), false, null);
        }

        if (!recoverFromArchive)
        {
            _ = databases.RequestCatchUp(session.DatabaseId, target);
            return (0, 0, false, string.Concat(nodeId, ": commit log gap; archive catch-up queued"), false, null);
        }

        var recovered = await recover(session, target, cancellationToken);
        databases.Observe(session.DatabaseId, session.CurrentBookmark);
        return recovered switch
        {
            ArchiveCatchUpProgress progress => (progress.Applied, progress.Applied, true, null, false, null),
            ClusterUnavailable unavailable => (0, 0, false, string.Concat(nodeId, ": ", unavailable.Message), false, null),
        };
    }

    private static bool IsTransient(QuicUnavailable unavailable) => unavailable.Code is "peer_unreachable" or "peer_draining" or "stream_reset";

    private (int Fetched, int Applied, bool More, string? Failure, bool TransportReset, CausalBookmark? Head) TimedOutPull(string nodeId, QuicTimeout timeoutError)
    {
        client.ResetPeer(nodeId);
        return (0, 0, false, string.Concat(nodeId, ": ", timeoutError.Message), false, null);
    }

    private async ValueTask<(int Fetched, int Applied, bool More, string? Failure, bool TransportReset, CausalBookmark? Head)> ApplyPageAsync(ISqlDatabaseSession session, string nodeId, CatchUpResponseMessage page, CancellationToken cancellationToken)
    {
        var head = page.Found ? page.Bookmark : null;
        if (!page.Found || page.Entries.Count == 0)
        {
            return (0, 0, false, null, false, head);
        }

        var applied = await replicator.ApplyToSessionAsync(session, page.Entries, requestCatchUpOnBuffered: false, cancellationToken);
        return applied switch
        {
            RemoteApplyOutcome outcome => (page.Entries.Count, outcome.Applied + outcome.Quarantined, page.HasMore, null, false, head),
            DatabaseNotFound notFound => (page.Entries.Count, 0, false, notFound.Message, false, head),
            SchemaMismatch mismatch => Stuck(session, nodeId, page, head, string.Concat(
                "schema epoch mismatch: expected ",
                mismatch.ExpectedEpoch.ToString(CultureInfo.InvariantCulture),
                ", commit ",
                mismatch.ActualEpoch.ToString(CultureInfo.InvariantCulture))),
            StorageCorrupted corrupted => Stuck(session, nodeId, page, head, corrupted.Message),
            StorageBusy busy => (page.Entries.Count, 0, false, busy.Message, false, head),
            ClusterUnavailable unavailable => (page.Entries.Count, 0, false, unavailable.Message, false, head),
        };
    }

    private (int Fetched, int Applied, bool More, string? Failure, bool TransportReset, CausalBookmark? Head) Stuck(
        ISqlDatabaseSession session, string nodeId, CatchUpResponseMessage page, CausalBookmark? head, string reason)
    {
        ClusterLog.CatchUpApplyFailed(logger, session.DatabaseId, nodeId, page.Entries.Count, reason);
        return (page.Entries.Count, 0, false, reason, false, head);
    }

    private static QuicRemoteError Gap(CausalBookmark available, string origin, long sequence)
    {
        var bookmark = available.Encode();
        return new QuicRemoteError(CommitLogGapCode, bookmark.Length <= CausalBookmark.MaxEncodedLength
            ? bookmark
            : CausalBookmark.ForRegion(origin, sequence).Encode());
    }

    private sealed record PeerSyncResult(int Fetched, int Applied, IReadOnlyList<string> Failures, bool Complete, bool HeadEmpty);

    private const long CatchUpResponseBytes = QuicProtocol.MaxPayloadBytes / 2;

    private static List<CommitMetadata> CausalPage(Dictionary<string, Queue<CommitMetadata>> pages, CausalBookmark have)
    {
        Dictionary<string, long> applied = new(StringComparer.Ordinal);
        long Applied(string origin) => applied.TryGetValue(origin, out var sequence) ? sequence : have.Get(origin);
        List<CommitMetadata> entries = [];
        long bytes = 0;
        var progressed = true;
        while (progressed)
        {
            progressed = false;
            foreach (var (origin, queue) in pages)
            {
                if (!queue.TryPeek(out var head) || head.Sequence != Applied(origin) + 1
                    || head.Bookmark.Entries.Any(dependency => !string.Equals(dependency.Key, origin, StringComparison.Ordinal) && Applied(dependency.Key) < dependency.Value))
                {
                    continue;
                }

                var size = EncodedSize(head);
                if (entries.Count > 0 && bytes + size > CatchUpResponseBytes)
                {
                    return entries;
                }

                entries.Add(queue.Dequeue());
                bytes += size;
                applied[origin] = head.Sequence;
                progressed = true;
            }
        }

        return entries;
    }

    private static long EncodedSize(CommitMetadata commit) =>
        commit.DeltaPayload.Length + (commit.OriginRegion.Length * 4L) + (commit.Bookmark.Entries.Count * 64L) + 96;

    private async ValueTask<QuicHandlerOutcome<CatchUpResponseMessage>> HandleCatchUpAsync(QuicCallContext context, CatchUpRequestMessage message, CancellationToken cancellationToken)
    {
        if (!topology.IsPeer(context, message.RequesterNodeId))
        {
            return ClusterTopology.PeerForbidden();
        }

        health.ObserveReachable(context.RemoteNodeId);
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
            Dictionary<string, Queue<CommitMetadata>> pages = new(StringComparer.Ordinal);
            var fetched = 0;
            var more = false;
            QuicRemoteError? refusal = null;
            for (var index = 0; index < lagging.Length; index++)
            {
                var (origin, sequence) = lagging[index];
                var from = message.Have.Get(origin) + 1;
                var remainingBudget = budget - fetched;
                var remainingOrigins = lagging.Length - index;
                var share = Math.Max(1, remainingBudget / remainingOrigins);
                var read = await session.ReadCommitLogAsync(origin, from, share, cancellationToken);
                var issue = read switch
                {
                    CommitLogPage { Entries.Count: > 0 } => null,
                    CommitLogPage or CommitLogGap => Gap(bookmark, origin, sequence),
                    StorageCorrupted corrupted => new QuicRemoteError("storage_corrupted", corrupted.Message),
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
                    CommitLogGap or StorageCorrupted or StorageBusy => [],
                };
                pages[origin] = new Queue<CommitMetadata>(page);
                fetched += page.Count;
                more |= page.Count > 0 && page[^1].Sequence < sequence;
            }

            var entries = CausalPage(pages, message.Have);
            more |= pages.Values.Any(static queue => queue.Count > 0);
            return entries.Count == 0 && refusal is not null
                ? refusal
                : new CatchUpResponseMessage(session.DatabaseId, true, bookmark, entries, more || refusal is not null);
        }
    }
}

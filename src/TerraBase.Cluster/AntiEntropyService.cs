using System.Globalization;
using Microsoft.Extensions.Logging;
using TerraScale.Contracts.Quic;
using TerraScale.QuicRpc;

namespace TerraBase.Cluster;

public sealed record CatchUpReport(Ulid DatabaseId, int Fetched, int Applied, int PeersContacted, IReadOnlyList<string> Failures)
{
    public static CatchUpReport Empty(Ulid databaseId) => new(databaseId, 0, 0, 0, []);
}

public sealed class AntiEntropyService(ClusterOptions options, ClusterDatabases databases, DeltaReplicator replicator, QuicRpcClient client, PeerHealthMonitor health, ILogger<AntiEntropyService> logger)
{
    private const int MaxPagesPerPeer = 64;

    public void Map(QuicRpcHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        _ = host.MapUnary<CatchUpRequestMessage, CatchUpResponseMessage>(QuicMessageType.CatchUpRequest, HandleCatchUpAsync);
    }

    public async Task RunPeriodicAsync(TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
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

    public async ValueTask<CatchUpReport> SyncAsync(Ulid databaseId, CancellationToken cancellationToken)
    {
        var resolved = await databases.ResolveAsync(databaseId, cancellationToken).ConfigureAwait(false);
        return resolved switch
        {
            ISqlDatabaseSession session => await SyncLeasedAsync(session, cancellationToken).ConfigureAwait(false),
            NotFound notFound => new CatchUpReport(databaseId, 0, 0, 0, [notFound.Message ?? "database not found"]),
            Unavailable unavailable => new CatchUpReport(databaseId, 0, 0, 0, [unavailable.Message]),
        };
    }

    private async ValueTask<CatchUpReport> SyncLeasedAsync(ISqlDatabaseSession session, CancellationToken cancellationToken)
    {
        await using (session.ConfigureAwait(false))
        {
            var report = CatchUpReport.Empty(session.DatabaseId);
            foreach (var peer in options.Peers)
            {
                if (!health.IsReachable(peer.NodeId))
                {
                    continue;
                }

                report = report with { PeersContacted = report.PeersContacted + 1 };
                for (var page = 0; page < MaxPagesPerPeer; page++)
                {
                    var (fetched, applied, more, failure) = await PullAsync(session, peer.NodeId, cancellationToken).ConfigureAwait(false);
                    report = report with
                    {
                        Fetched = report.Fetched + fetched,
                        Applied = report.Applied + applied,
                        Failures = failure is null ? report.Failures : [.. report.Failures, failure],
                    };
                    if (!more || failure is not null || applied == 0)
                    {
                        break;
                    }
                }
            }

            if (report.Applied > 0)
            {
                ClusterLog.CatchUpApplied(logger, session.DatabaseId, report.Applied, report.Fetched, session.CurrentBookmark);
            }

            return report;
        }
    }

    private async ValueTask<(int Fetched, int Applied, bool More, string? Failure)> PullAsync(ISqlDatabaseSession session, string nodeId, CancellationToken cancellationToken)
    {
        var request = new CatchUpRequestMessage(session.DatabaseId, options.NodeId, session.CurrentBookmark, options.CatchUpPageSize);
        var response = await client.CallAsync<CatchUpRequestMessage, CatchUpResponseMessage>(nodeId, QuicMessageType.CatchUpRequest, request, options.CatchUpTimeout, cancellationToken).ConfigureAwait(false);
        return response switch
        {
            CatchUpResponseMessage page => await ApplyPageAsync(session, page, cancellationToken).ConfigureAwait(false),
            Unavailable unavailable => (0, 0, false, string.Concat(nodeId, ": ", unavailable.Message)),
            RequestTimeout timeout => (0, 0, false, string.Concat(nodeId, ": ", timeout.Message)),
            QuicRemoteError error => (0, 0, false, string.Concat(nodeId, ": ", error.Message)),
        };
    }

    private async ValueTask<(int Fetched, int Applied, bool More, string? Failure)> ApplyPageAsync(ISqlDatabaseSession session, CatchUpResponseMessage page, CancellationToken cancellationToken)
    {
        if (!page.Found || page.Entries.Count == 0)
        {
            return (0, 0, false, null);
        }

        var applied = await replicator.ApplyToSessionAsync(session, page.Entries, cancellationToken).ConfigureAwait(false);
        return applied switch
        {
            RemoteApplyOutcome outcome => (page.Entries.Count, outcome.Applied, page.HasMore, null),
            NotFound notFound => (page.Entries.Count, 0, false, notFound.Message ?? "database not found"),
            SchemaMismatch mismatch => (page.Entries.Count, 0, false, string.Concat("schema epoch mismatch: expected ", mismatch.ExpectedEpoch.ToString(CultureInfo.InvariantCulture))),
            StorageCorrupted corrupted => (page.Entries.Count, 0, false, corrupted.Message),
            StorageBusy busy => (page.Entries.Count, 0, false, busy.Message),
            Unavailable unavailable => (page.Entries.Count, 0, false, unavailable.Message),
        };
    }

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
            List<CommitMetadata> entries = [];
            var more = false;
            foreach (var (origin, sequence) in bookmark.Entries)
            {
                var from = message.Have.Get(origin) + 1;
                if (sequence < from)
                {
                    continue;
                }

                if (entries.Count >= budget)
                {
                    more = true;
                    break;
                }

                var read = await session.ReadCommitLogAsync(origin, from, budget - entries.Count, cancellationToken).ConfigureAwait(false);
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

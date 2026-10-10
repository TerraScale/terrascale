using System.Text.Json;
using System.Text.Json.Serialization;
using TerraBase.Engine;
using TerraBase.Hosting;

namespace TerraBase.Features.Stats;

/// <summary>NATS subjects of a cell's live statistics on ts-orchestrator's fabric.</summary>
internal static class StatsSubjects
{
    /// <summary>Where every window's rollup is published.</summary>
    internal static string Rollup(string cell) => $"ts.orch.v1.{cell}.obs.terrabase";

    /// <summary>Where a detail request is answered, once by every member of the cell.</summary>
    internal static string Detail(string cell) => $"ts.orch.v1.{cell}.query.terrabase-stats.detail";
}

/// <summary>
/// One database, one scope or a member's totals (v1). Counters are over one window; <see cref="Latency"/> bins follow
/// <see cref="StatsLatency.BoundsMs"/>. Nullable members are omitted when unknown or not applicable.
/// </summary>
internal sealed record StatsEntryV1(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Org,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Project,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Database,
    long Reads, long Writes, long Requests, long Errors, long SentBytes, long ReceivedBytes, long LagGroups, double LagSeconds, long Conflicts,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? StorageBytes,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long[]? Latency,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<StatsPeerV1>? Peers,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<StatsCollectionV1>? Collections);

/// <summary>One peer region of a member or a database: bytes moved in the window, and the latest lag and divergence.</summary>
internal sealed record StatsPeerV1(string Region, long SentBytes, long ReceivedBytes, long LagGroups, double LagSeconds, long Conflicts);

/// <summary>One collection of a database: reads and writes in the window, and its documents when counted in time.</summary>
internal sealed record StatsCollectionV1(string Name, long Reads, long Writes,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? Documents, bool Complete);

/// <summary>The rollup a member publishes every window on <see cref="StatsSubjects.Rollup"/>.</summary>
internal sealed record StatsRollupV1(int V, string Id, string Epoch, long Seq, string Cell, string Region, string Member, DateTimeOffset At,
    long WindowMs, int Databases, StatsEntryV1 Totals, IReadOnlyList<StatsPeerV1> Peers, IReadOnlyList<StatsEntryV1> Top);

/// <summary>A detail request on <see cref="StatsSubjects.Detail"/>: one database with its collections, or a page of databases.</summary>
internal sealed record StatsDetailRequestV1(int V, string? Database, string? After);

/// <summary>One member's answer to a detail request, over its last closed window.</summary>
internal sealed record StatsDetailV1(int V, string Cell, string Region, string Member, string Epoch, DateTimeOffset At, long WindowMs,
    IReadOnlyList<StatsEntryV1> Databases, string? Next);

/// <summary>The answer to a request this member cannot read.</summary>
internal sealed record StatsErrorV1(int V, string Error);

/// <summary>Builds the v1 rollup and detail answers from closed windows. Every list is capped so one message stays small.</summary>
internal static class StatsRollup
{
    // Each ranking contributes its top 20 to the rollup's top list, which therefore holds at most 60 databases.
    private const int TopPerRanking = 20;
    private const int MaxDetailDatabases = 500;
    private const int MaxDetailPeers = 4_096;
    private const int MaxCollections = 256;
    private const int MaxRequestBytes = 4 * 1024;
    // The examined rows of all collections' counts in one detail answer, and the time they may take.
    private const long ExaminedBudget = 1_000_000;
    private static readonly TimeSpan CountTime = TimeSpan.FromSeconds(2);

    /// <summary>The rollup of <paramref name="window"/>, as this member publishes it.</summary>
    internal static StatsRollupV1 Build(StatsWindow window, string cell, string region, string member, string epoch, long seq) =>
        new(1, Ulid.NewUlid().ToString(), epoch, seq, cell, region, member, window.At, Milliseconds(window.Duration), window.Databases,
            Counts(window.Totals, null, null, null, null, null), Peers(window.Peers), Top(window.Active));

    internal static long Milliseconds(TimeSpan span) => (long)span.TotalMilliseconds;

    /// <summary>The request, or null when it is larger than 4 KiB, unparsable or not v1.</summary>
    internal static StatsDetailRequestV1? ParseRequest(ReadOnlyMemory<byte> data)
    {
        if (data.Length > MaxRequestBytes)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize(data.Span, TerraBaseJsonContext.Default.StatsDetailRequestV1) is { V: 1 } request ? request : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The databases after <paramref name="after"/> in ID order, up to the reply's caps; <c>next</c> is the last one returned when more remain.</summary>
    internal static (IReadOnlyList<StatsEntryV1> Databases, string? Next) Page(StatsWindow? window, string? after)
    {
        var candidates = (window?.Active ?? [])
            .Where(database => database.Database.DatabaseId is { } id && (after is null || string.CompareOrdinal(id, after) > 0))
            .OrderBy(database => database.Database.DatabaseId, StringComparer.Ordinal)
            .ToList();
        var page = new List<StatsEntryV1>();
        var peers = 0;
        foreach (var database in candidates)
        {
            if (page.Count == MaxDetailDatabases || peers + database.Peers.Count > MaxDetailPeers)
            {
                break;
            }

            page.Add(Database(database));
            peers += database.Peers.Count;
        }

        return (page, page.Count < candidates.Count ? page[^1].Database : null);
    }

    /// <summary>The database with <paramref name="databaseId"/> when the window tracks it, with its collections counted.</summary>
    internal static async ValueTask<(IReadOnlyList<StatsEntryV1> Databases, string? Next)> DatabaseAsync(
        IStorageEngine engine, StatsWindow? window, string databaseId, CancellationToken cancellationToken)
    {
        if (window?.Active.FirstOrDefault(database => string.Equals(database.Database.DatabaseId, databaseId, StringComparison.Ordinal)) is not { } found)
        {
            return ([], null);
        }

        return ([Database(found, await CollectionsAsync(engine, found, cancellationToken).ConfigureAwait(false))], null);
    }

    // The busiest databases by replication bytes, by operations and by storage, as one list ordered by replication, then operations, then ID.
    private static StatsEntryV1[] Top(IReadOnlyList<DatabaseWindow> active) =>
        [.. active.OrderByDescending(Replication).Take(TopPerRanking)
            .Union(active.OrderByDescending(Operations).Take(TopPerRanking))
            .Union(active.Where(database => database.Counters.StorageBytes is not null)
                .OrderByDescending(database => database.Counters.StorageBytes).Take(TopPerRanking))
            .OrderByDescending(Replication).ThenByDescending(Operations).ThenBy(database => database.Database.DatabaseId, StringComparer.Ordinal)
            .Select(database => Database(database))];

    private static long Replication(DatabaseWindow database) => database.Counters.SentBytes + database.Counters.ReceivedBytes;

    private static long Operations(DatabaseWindow database) => database.Counters.Reads + database.Counters.Writes;

    private static StatsEntryV1 Database(DatabaseWindow database, IReadOnlyList<StatsCollectionV1>? collections = null) =>
        Counts(database.Counters, database.Database.OrganizationId, database.Database.ProjectId, database.Database.DatabaseId, Peers(database.Peers),
            collections);

    private static StatsEntryV1 Counts(StatsCounters counters, string? org, string? project, string? database,
        IReadOnlyList<StatsPeerV1>? peers, IReadOnlyList<StatsCollectionV1>? collections) =>
        new(org, project, database, counters.Reads, counters.Writes, counters.Requests, counters.Errors, counters.SentBytes,
            counters.ReceivedBytes, counters.LagGroups, counters.LagSeconds, counters.Conflicts, counters.StorageBytes, counters.Latency,
            peers, collections);

    private static IReadOnlyList<StatsPeerV1> Peers(IReadOnlyList<PeerCounters> peers) =>
        [.. peers.Select(peer => new StatsPeerV1(peer.Region, peer.SentBytes, peer.ReceivedBytes, peer.LagGroups, peer.LagSeconds, peer.Conflicts))];

    // Lists the database's collections, then counts each within one examined-rows budget and CountTime. Collections
    // left over once either is spent are reported incomplete, without a count.
    private static async ValueTask<IReadOnlyList<StatsCollectionV1>> CollectionsAsync(
        IStorageEngine engine, DatabaseWindow database, CancellationToken cancellationToken)
    {
        if (await engine.BeginTransactionAsync(database.Database, write: false, cancellationToken).ConfigureAwait(false) is not { } transaction)
        {
            return [];
        }

        await using (transaction.ConfigureAwait(false))
        {
            var (listed, _) = await transaction.ListCollectionsAsync(null, MaxCollections, cancellationToken).ConfigureAwait(false);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(CountTime);
            var remaining = ExaminedBudget;
            var collections = new List<StatsCollectionV1>(listed.Count);
            foreach (var collection in listed)
            {
                var counts = database.Collections.FirstOrDefault(window => string.Equals(window.Name, collection.Name, StringComparison.Ordinal));
                long? documents = null;
                var complete = false;
                if (remaining > 0 && !deadline.IsCancellationRequested)
                {
                    try
                    {
                        var (count, finished) = await transaction.CountAsync(collection.Name, null, remaining, deadline.Token).ConfigureAwait(false);
                        documents = count;
                        complete = finished;
                        remaining -= count;
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        // The time is spent: this collection and the rest stay uncounted.
                    }
                }

                collections.Add(new StatsCollectionV1(collection.Name, counts?.Reads ?? 0, counts?.Writes ?? 0, documents, complete));
            }

            return collections;
        }
    }
}

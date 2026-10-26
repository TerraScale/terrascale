using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Text.Json;
using TerraBase.Configuration;
using TerraBase.Engine;
using TerraBase.Errors;
using TerraBase.Features.Memory;
using TerraBase.Features.Stats;
using TerraBase.Hosting;
using TerraBase.Primitives;
using TerraBase.Tenancy;
using ZLogger;
using static TerraBase.Features.Management.SystemCatalog;

namespace TerraBase.Features.Management.Usage;

/// <summary>The dimensions an observation read keeps, and the memory admission charged, in bytes, for what it keeps.</summary>
/// <param name="Keep">Whether a read keeps a dimension.</param>
/// <param name="Admit">Admits memory for what a read keeps.</param>
/// <param name="Hold">Holds admission for a whole document while it is parsed, before Keep filters it.</param>
internal sealed record ObservationFilter(Func<UsageDimension, bool> Keep, Action<long> Admit, Func<long, IDisposable> Hold);

/// <summary>Only the read and write totals of a persisted <see cref="OpCounts"/>; its meters are skipped, not materialized.</summary>
internal sealed record UsageTotals(long Reads, long Writes)
{
    public OpCounts Counts => new(Reads, Writes);
}

/// <summary>
/// Operation counts; persisted per managed database as document <c>ops~node</c> of its <c>usage</c> state collection
/// (lifetime), and as one document <c>bucket~node</c> per UTC hour and per UTC day of its <c>usage-hours</c> and
/// <c>usage-days</c> collections. Reads sum every node's documents.
/// </summary>
/// <remarks>
/// The catalog replicates to every region and keeps the last write of a document, so each node adds only to documents
/// named by itself: two regions' flushes never overwrite each other's counts.
/// </remarks>
/// <param name="Reads">Document reads.</param>
/// <param name="Writes">Document writes.</param>
/// <param name="Meters">Regional meter observations keyed by their bounded dimensions.</param>
/// <param name="ReadUnits">Operation units (<see cref="OperationUnits"/>) of the reads.</param>
/// <param name="WriteUnits">Operation units of the writes.</param>
internal sealed record OpCounts(long Reads, long Writes, IReadOnlyDictionary<string, UsageReading>? Meters = null, long ReadUnits = 0, long WriteUnits = 0)
{
    public static OpCounts Zero { get; } = new(0, 0);

    public static OpCounts operator +(OpCounts left, OpCounts right)
    {
        return new(left.Reads + right.Reads, left.Writes + right.Writes, UsageReading.Combine(left.Meters, right.Meters, 1),
            left.ReadUnits + right.ReadUnits, left.WriteUnits + right.WriteUnits);
    }

    public static OpCounts operator -(OpCounts left, OpCounts right)
    {
        return new(left.Reads - right.Reads, left.Writes - right.Writes, UsageReading.Combine(left.Meters, right.Meters, -1),
            left.ReadUnits - right.ReadUnits, left.WriteUnits - right.WriteUnits);
    }
}

/// <summary>Operation units of a UTC month, or why they cannot be read now.</summary>
internal readonly union MonthUnitsResult(UsageTotals, Unavailable);

/// <summary>Which buckets counts belong in.</summary>
internal enum UsageScope
{
    /// <summary>Their hour, and its day.</summary>
    Hour,

    /// <summary>Only the day that starts at their hour: counts held past the kept hours.</summary>
    Day,

    /// <summary>Only the lifetime totals: counts held past the kept days.</summary>
    Lifetime,

    /// <summary>The minute, hour and day.</summary>
    Minute,
}

/// <summary>The counts of one database in one UTC hour.</summary>
/// <param name="Database">The database.</param>
/// <param name="Hour">Hours since the Unix epoch.</param>
/// <param name="Scope">Which buckets the counts belong in.</param>
/// <param name="Minute">Minute within the UTC hour for minute-scoped observations.</param>
internal readonly record struct UsageKey(DatabaseOwner Database, long Hour, UsageScope Scope = UsageScope.Hour, int Minute = 0)
{
    /// <summary>Whether the counts belong in a bucket of <paramref name="granularity"/>.</summary>
    public bool In(UsageGranularity granularity) => granularity == UsageWindow.Minute ? Scope == UsageScope.Minute
        : Scope is UsageScope.Hour or UsageScope.Minute || (Scope == UsageScope.Day && granularity == UsageWindow.Day);

    public long Bucket(UsageGranularity granularity) => (Hour * 60 + Minute) / granularity.Minutes;
}

/// <summary>Usage counts of a database: the lifetime totals and one entry per bucket of the window read.</summary>
/// <param name="Lifetime">The lifetime totals.</param>
/// <param name="Series">One entry per bucket.</param>
internal sealed record UsageCounts(OpCounts Lifetime, OpCounts[] Series);

/// <summary>Usage counts, or why they cannot be read now.</summary>
internal readonly union UsageCountsResult(UsageCounts, Unavailable);

/// <summary>
/// Counts operations in memory per database (organization, project and database: the request's tenant) and UTC hour, and
/// adds them to the catalog on <see cref="FlushAsync"/>. Counts of a database the catalog no longer names are dropped at the
/// flush.
/// </summary>
/// <remarks>
/// Memory holds one entry per database and hour with operations since the last flush. Before each flush, entries past the
/// kept hours fold into a day-only entry, and those past the kept days into a lifetime-only one, so a backlog (a catalog
/// that refuses writes) holds at most about 1,250 entries per database. Entries are indexed by database; a flush drops the
/// maps it emptied.
/// </remarks>
internal sealed partial class UsageMeter(SystemCatalog catalog, TimeProvider time, TerraBaseOptions options, ILogger<UsageMeter> logger, IMemoryBudget memory, IMeterFactory meters,
    IDatabaseStats stats)
    : IUsageMeter, IDisposable
{
    // Byte meters aggregate past every count limit (a day of storage samples, lifetime bandwidth); they are bounded only by long.
    internal static bool ByteMeter(string meter) => meter is "client_bytes" or "replication_bytes" or "storage_bytes";

    private const string Ops = "ops";

    // The id of this node's last flush that added to a database's counts, saved in the same write.
    private const string LastFlush = "flush";

    // Separates a document's name from the node that alone writes it; sorts after every id character before it.
    private const char NodeSeparator = '~';

    // Bucket documents are a few dozen bytes; this only bounds a page if one ever is not.
    private const long MaxPageBytes = 1024 * 1024;

    // Most databases one flush transaction saves. Batch 16 (2026-10-26): one transaction for every database's counts held
    // SYSTEM's writer for up to 4.3 s on gru, and catalog anchors and replication applies queued behind it.
    internal const int FlushDatabases = 16;

    // Best-effort dashboard usage, not billing: counts live only here until a flush saves them, so a crash loses at most
    // those counted since the last successful flush. A failed flush keeps them for the next; a clean shutdown flushes.
    private readonly ConcurrentDictionary<DatabaseOwner, ConcurrentDictionary<UsageKey, OpCounts>> _pending = new();

    // Adds hold it shared; dropping emptied databases' maps holds it exclusively, so no Add writes to a dropped map.
    private readonly ReaderWriterLockSlim _maps = new();

    // Makes a flush (catalog commit plus removal from memory) atomic for readers, so totals never count twice or miss.
    private readonly SemaphoreSlim _flush = new(1, 1);

    // A flush whose write threw may still have committed (the catalog commits before it disposes the transaction): its
    // counts stay pending until its id, saved with them, shows whether they were saved. Guarded by _flush.
    private (string Id, KeyValuePair<UsageKey, OpCounts>[] Batch)? _inDoubt;

    public void RecordRead(TenantContext database, long units)
    {
        Buffer(new(DatabaseOwner.Of(database), CurrentHour()), 1, 0, units, 0);
        CountMonth(database, units);
        RecordOperation(database, "read", 1);
        stats.Read(database);
    }

    public void RecordWrites(TenantContext database, int writes, long units)
    {
        Buffer(new(DatabaseOwner.Of(database), CurrentHour()), 0, writes, 0, units);
        CountMonth(database, units);
        stats.Wrote(database, writes);
    }

    /// <summary>Databases with counts in memory.</summary>
    internal int PendingDatabases
    {
        get
        {
            Publish();
            return _pending.Count;
        }
    }

    /// <summary>Adds counts.</summary>
    public void Add(UsageKey key, OpCounts counts)
    {
        if (counts.Meters is not null)
        {
            AddMeasurements(key, counts);
            return;
        }

        AddCore(key, counts);
    }

    private void AddCore(UsageKey key, OpCounts counts)
    {
        if (counts == OpCounts.Zero)
        {
            return;
        }

        _maps.EnterReadLock();
        try
        {
            _pending.GetOrAdd(key.Database, _ => new()).AddOrUpdate(key, counts, (_, current) => current + counts);
        }
        finally
        {
            _maps.ExitReadLock();
        }
    }

    /// <summary>
    /// Persisted plus not yet flushed counts: the lifetime totals and one entry per bucket of <paramref name="window"/>.
    /// <paramref name="proceed"/>, when set, runs before each catalog read (once a flush let this read in, after settling one
    /// in doubt, and before each page of the series) and throws to start none.
    /// </summary>
    public async ValueTask<UsageCountsResult> GetAsync(
        TenantContext database, UsageWindow window, CancellationToken cancellationToken, Action? proceed = null)
    {
        return await ReadAsync(database, window, meters: false, filter: null, proceed, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Like <see cref="GetAsync"/>, with regional meters. <paramref name="filter"/>, when set, keeps only its dimensions of
    /// each entry as it is read and admits memory for what it keeps.
    /// </summary>
    public async ValueTask<UsageCountsResult> GetObservationsAsync(
        TenantContext database, UsageWindow window, CancellationToken cancellationToken, Action? proceed = null, ObservationFilter? filter = null) =>
        await ReadAsync(database, window, meters: true, filter, proceed, cancellationToken).ConfigureAwait(false);

    // Counts-only reads skip every entry's meters as it is read, so usage totals retain no analytics observations.
    private async ValueTask<UsageCountsResult> ReadAsync(
        TenantContext database, UsageWindow window, bool meters, ObservationFilter? filter, Action? proceed, CancellationToken cancellationToken)
    {

        OpCounts Shape(OpCounts counts)
        {
            return !meters ? Strip(counts, meters) : filter is null ? counts : Retain(counts, filter);
        }

        // Counts-only reads skip the meters JSON; analytics reads hold admission for the whole document while parsing it.
        OpCounts Parse(StoredDocument document)
        {
            if (!meters)
            {
                return JsonSerializer.Deserialize(document.Json.Span, TerraBaseJsonContext.Default.UsageTotals)!.Counts;
            }

            using var parsing = filter?.Hold(document.Json.Length * ParsedBytesPerJsonByte);
            return Shape(JsonSerializer.Deserialize(document.Json.Span, TerraBaseJsonContext.Default.OpCounts)!);
        }

        var owner = DatabaseOwner.Of(database);
        await _flush.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Publish();
            proceed?.Invoke();
            if (_inDoubt is not null)
            {
                if (await SettleAsync(cancellationToken).ConfigureAwait(false) is Unavailable refused)
                {
                    return refused;
                }
                proceed?.Invoke();
            }

            var read = await catalog.ReadAsync<UsageCountsResult>(
                async transaction => new UsageCounts(
                    await ReadLifetimeAsync(transaction, owner.DatabaseId, Parse, cancellationToken).ConfigureAwait(false),
                    await ReadSeriesAsync(transaction, owner.DatabaseId, window, Parse, proceed, cancellationToken).ConfigureAwait(false)),
                static error => error, cancellationToken).ConfigureAwait(false);
            if (read is not UsageCounts saved)
            {
                return read switch
                {
                    Unavailable error => error,
                    UsageCounts or null => throw new UnreachableException(),
                };
            }

            var (lifetime, series) = saved;
            foreach (var (key, entry) in _pending.TryGetValue(owner, out var pending) ? pending : [])
            {
                var counts = Shape(entry);
                lifetime += counts;
                if (key.In(window.Granularity) && window.IndexOf(key.Hour, key.Minute) is { } index)
                {
                    series[index] += counts;
                }
            }

            // Only kept buckets have counts, saved or not: a saved bucket the next flush would prune reads as zero.
            var oldestKept = OldestKept(window.Granularity, CurrentMinute());
            for (var i = 0; i < series.Length && window.First + i < oldestKept; i++)
            {
                series[i] = OpCounts.Zero;
            }

            return new UsageCounts(lifetime, series);
        }
        finally
        {
            _flush.Release();
        }
    }

    /// <summary>Adds pending counts to the catalog in one transaction. On failure the counts stay pending.</summary>
    public async ValueTask FlushAsync(CancellationToken cancellationToken)
    {
        // The snapshot is taken under the lock, so overlapping flushes never persist the same counts twice.
        await _flush.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Publish();
            await FlushLockedAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _flush.Release();
        }
    }

    private async ValueTask FlushLockedAsync(CancellationToken cancellationToken)
    {
        Compact();
        if (await SettleAsync(cancellationToken).ConfigureAwait(false) is Unavailable)
        {
            return;
        }
        KeyValuePair<UsageKey, OpCounts>[] batch = [.. Entries()];
        if (batch.Length == 0)
        {
            return;
        }

        // Empty flushes never initialize the catalog. Accept it before saving a nonempty batch.
        if (await catalog.CheckAsync(cancellationToken).ConfigureAwait(false) is Unavailable)
        {
            return;
        }

        // One transaction per chunk of databases, each with its own id: a chunk that throws is in doubt alone, and the
        // chunks after it stay pending for the next flush.
        var dropped = 0;
        foreach (var databases in batch.GroupBy(entry => entry.Key.Database).Chunk(FlushDatabases))
        {
            KeyValuePair<UsageKey, OpCounts>[] chunk = [.. databases.SelectMany(entries => entries)];
            var id = EntityIds.New().ToString();
            try
            {
                if (await WriteAsync(id, chunk, cancellationToken).ConfigureAwait(false) is not int saved)
                {
                    break;
                }

                dropped += saved;
            }
            catch
            {
                _inDoubt = (id, chunk);
                throw;
            }

            foreach (var (key, counts) in chunk)
            {
                Subtract(key, counts);
            }
        }

        DropEmptied();

        if (dropped > 0)
        {
            logger.ZLogDebug($"Dropped operation counts of {dropped:@Databases} deleted databases");
        }
    }

    // Decides a flush that threw: it committed if any database it saved holds its id (one transaction saves all or none),
    // and then its counts leave memory; otherwise they stay pending for the next flush. A failed read leaves it in doubt.
    // Only a flush that accepted the catalog first leaves one in doubt.
    private async ValueTask<CatalogCheck> SettleAsync(CancellationToken cancellationToken)
    {
        if (_inDoubt is not { } doubt)
        {
            return new Done();
        }

        var committed = await catalog.ReadAsync<SettleResult>(async transaction =>
        {
            foreach (var database in doubt.Batch.Select(entry => entry.Key.Database.DatabaseId).Distinct())
            {
                if (await SystemCatalog.GetAsync(transaction, UsageCollection(database), Own(LastFlush), TerraBaseJsonContext.Default.String, cancellationToken)
                    .ConfigureAwait(false) == doubt.Id)
                {
                    return true;
                }
            }

            return false;
        }, static error => error, cancellationToken).ConfigureAwait(false);
        if (committed is not bool settled)
        {
            return committed switch
            {
                Unavailable refused => refused,
                bool => throw new UnreachableException(),
            };
        }

        if (settled)
        {
            foreach (var (key, counts) in doubt.Batch)
            {
                Subtract(key, counts);
            }
        }

        _inDoubt = null;
        return new Done();
    }

    // Returns how many databases of the batch no longer exist.
    private ValueTask<FlushResult> WriteAsync(
        string id, KeyValuePair<UsageKey, OpCounts>[] batch, CancellationToken cancellationToken)
    {
        var minute = CurrentMinute();
        return catalog.WriteAsync<FlushResult>(async transaction =>
        {
            // Billed months keep the units of databases deleted since. Lifetime-only entries are past every kept day.
            foreach (var month in batch.Where(entry => entry.Key.Scope != UsageScope.Lifetime && entry.Value.ReadUnits + entry.Value.WriteUnits != 0)
                .GroupBy(entry => (entry.Key.Database.OrganizationId, entry.Key.Database.ProjectId, Month: MonthId(entry.Key.Hour))))
            {
                var units = new UsageTotals(month.Sum(entry => entry.Value.ReadUnits), month.Sum(entry => entry.Value.WriteUnits));
                await AddUnitsAsync(transaction, ProjectMonths(month.Key.ProjectId), Own(month.Key.Month), units, cancellationToken).ConfigureAwait(false);
                await AddUnitsAsync(transaction, OrganizationMonths(month.Key.OrganizationId), Own(month.Key.Month), units, cancellationToken).ConfigureAwait(false);
            }

            var dropped = 0;
            foreach (var entries in batch.GroupBy(entry => entry.Key.Database))
            {
                var database = entries.Key;
                // Checked inside the write, which the engine serializes with a database delete: a delete that commits
                // later removes these counts with the database's state; one that committed earlier removed the database
                // from its project. Either way a deleted database gets no usage back. One point read per level.
                if (await EngineManagementService.DatabaseTenantUncheckedAsync(
                    transaction, database.OrganizationId, database.ProjectId, database.DatabaseId, cancellationToken).ConfigureAwait(false) is null)
                {
                    dropped++;
                    continue;
                }

                var usage = UsageCollection(database.DatabaseId);
                await AddAsync(transaction, usage, Own(Ops), entries.Aggregate(OpCounts.Zero, (sum, entry) => sum + entry.Value), cancellationToken).ConfigureAwait(false);
                await PutAsync(transaction, usage, Own(LastFlush), id, TerraBaseJsonContext.Default.String, cancellationToken).ConfigureAwait(false);
                foreach (var granularity in UsageWindow.Granularities)
                {
                    var buckets = Collection(database.DatabaseId, granularity);
                    // Buckets past retention (counts held through a long outage) only add to the lifetime totals, and each
                    // flush deletes every expired bucket of the databases it saves: so a database holds at most the kept
                    // buckets and those that expired since its last flush.
                    var oldestKept = OldestKept(granularity, minute);
                    foreach (var bucket in entries.Where(entry => entry.Key.In(granularity)).GroupBy(entry => entry.Key.Bucket(granularity))
                        .Where(bucket => bucket.Key >= oldestKept))
                    {
                        var counts = bucket.Aggregate(OpCounts.Zero, (sum, entry) => sum + entry.Value);
                        await AddAsync(transaction, buckets, Own(granularity.Id(bucket.Key)), counts, cancellationToken).ConfigureAwait(false);
                    }

                    await PruneAsync(transaction, buckets, granularity.Id(oldestKept), cancellationToken).ConfigureAwait(false);
                }
            }

            return dropped;
        }, static error => error, cancellationToken);
    }

    private static async ValueTask AddAsync(IStorageTransaction transaction, string collection, string id, OpCounts counts, CancellationToken cancellationToken)
    {
        var saved = await SystemCatalog.GetAsync(transaction, collection, id, TerraBaseJsonContext.Default.OpCounts, cancellationToken).ConfigureAwait(false);
        await PutAsync(transaction, collection, id, (saved ?? OpCounts.Zero) + counts, TerraBaseJsonContext.Default.OpCounts, cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask AddUnitsAsync(IStorageTransaction transaction, string collection, string id, UsageTotals units, CancellationToken cancellationToken)
    {
        var saved = await SystemCatalog.GetAsync(transaction, collection, id, TerraBaseJsonContext.Default.UsageTotals, cancellationToken).ConfigureAwait(false);
        await PutAsync(transaction, collection, id, new UsageTotals((saved?.Reads ?? 0) + units.Reads, (saved?.Writes ?? 0) + units.Writes),
            TerraBaseJsonContext.Default.UsageTotals, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Read and write operation units this UTC month of an organization, or of one of its projects when
    /// <paramref name="projectId"/> is set: every node's saved month plus this node's pending counts.
    /// </summary>
    internal async ValueTask<MonthUnitsResult> GetMonthAsync(string organizationId, string? projectId, CancellationToken cancellationToken)
    {
        var month = MonthId(CurrentHour());
        await _flush.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Publish();
            if (await SettleAsync(cancellationToken).ConfigureAwait(false) is Unavailable refused)
            {
                return refused;
            }

            var read = await catalog.ReadAsync<MonthUnitsResult>(async transaction =>
            {
                var collection = projectId is null ? OrganizationMonths(organizationId) : ProjectMonths(projectId);
                var units = new UsageTotals(0, 0);
                // Ids sort by month then node: a month's documents follow its bare id, one per node that flushed.
                var after = month;
                while (true)
                {
                    var page = await transaction.ListAsync(collection, after, limit: 100, MaxPageBytes, cancellationToken).ConfigureAwait(false);
                    foreach (var document in page.Documents)
                    {
                        if (Name(document.Id) != month)
                        {
                            return units;
                        }

                        var saved = JsonSerializer.Deserialize(document.Json.Span, TerraBaseJsonContext.Default.UsageTotals)!;
                        units = new(units.Reads + saved.Reads, units.Writes + saved.Writes);
                    }

                    if (!page.HasMore)
                    {
                        return units;
                    }

                    after = page.Documents[^1].Id;
                }
            }, static error => error, cancellationToken).ConfigureAwait(false);
            if (read is not UsageTotals total)
            {
                return read switch
                {
                    Unavailable error => error,
                    UsageTotals => throw new UnreachableException(),
                };
            }

            foreach (var (key, counts) in Entries())
            {
                if (key.Database.OrganizationId == organizationId && (projectId is null || key.Database.ProjectId == projectId)
                    && key.Scope != UsageScope.Lifetime && MonthId(key.Hour) == month)
                {
                    total = new(total.Reads + counts.ReadUnits, total.Writes + counts.WriteUnits);
                }
            }

            return total;
        }
        finally
        {
            _flush.Release();
        }
    }

    // Bucket ids sort by time, so the oldest come first. Bounded by what one database can hold (see WriteAsync).
    private static async ValueTask PruneAsync(IStorageTransaction transaction, string collection, string oldestKept, CancellationToken cancellationToken)
    {
        while (true)
        {
            var page = await transaction.ListAsync(collection, afterId: null, limit: 100, MaxPageBytes, cancellationToken).ConfigureAwait(false);
            var expired = page.Documents.TakeWhile(document => string.CompareOrdinal(document.Id, oldestKept) < 0).ToList();
            foreach (var document in expired)
            {
                await transaction.DeleteAsync(collection, document.Id, cancellationToken).ConfigureAwait(false);
            }

            if (expired.Count < page.Documents.Count || !page.HasMore)
            {
                return;
            }
        }
    }

    // The first bucket a flush still saves: the kept buckets end with the current one.
    private static long OldestKept(UsageGranularity granularity, long minute) => (minute / granularity.Minutes) - granularity.Kept + 1;

    // Folds entries past the kept hours into a day-only entry at their day's first hour, and those past the kept days into
    // a lifetime-only entry at hour 0; a concurrent Add leaves an entry for the next pass. Under _flush. The keys of a batch
    // in doubt keep their counts, which settling it subtracts.
    private void Compact()
    {
        var minute = CurrentMinute();
        var hour = minute / 60;
        var held = (_inDoubt?.Batch ?? []).Select(entry => entry.Key).ToHashSet();
        foreach (var pending in _pending.Values)
        {
            foreach (var (key, counts) in pending)
            {
                var folded = key.Scope == UsageScope.Minute && key.Hour * 60 + key.Minute < OldestKept(UsageWindow.Minute, minute)
                    ? key with { Scope = UsageScope.Hour, Minute = 0 }
                    : key.Scope == UsageScope.Lifetime ? key
                    : key.Hour / 24 < OldestKept(UsageWindow.Day, minute) ? key with { Hour = 0, Scope = UsageScope.Lifetime }
                    : key.Scope == UsageScope.Hour && key.Hour < OldestKept(UsageWindow.Hour, minute) ? key with { Hour = key.Hour / 24 * 24, Scope = UsageScope.Day }
                    : key;
                if (folded != key && !held.Contains(key))
                {
                    FoldMeasurements(key, folded, counts);
                }
            }
        }
    }

    private IEnumerable<KeyValuePair<UsageKey, OpCounts>> Entries() => _pending.Values.SelectMany(pending => pending);

    // Drops the maps of databases with nothing pending, so a database counted once holds no memory for good. Under _flush.
    private void DropEmptied()
    {
        _maps.EnterWriteLock();
        try
        {
            foreach (var (owner, pending) in _pending)
            {
                if (pending.IsEmpty)
                {
                    _pending.TryRemove(owner, out _);
                }
            }
        }
        finally
        {
            _maps.ExitWriteLock();
        }
    }

    private readonly union SettleResult(bool, Unavailable);
    private readonly union FlushResult(int, Unavailable);

    // proceed runs before each page, so a read that ends past a request's deadline starts no further one.
    private static OpCounts Strip(OpCounts counts, bool meters) => meters || counts.Meters is null ? counts : counts with { Meters = null };

    private static OpCounts Retain(OpCounts counts, ObservationFilter filter)
    {
        if (counts.Meters is null)
        {
            return counts;
        }

        var kept = counts.Meters.Where(entry => filter.Keep(entry.Value.Dimension)).ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);
        filter.Admit(kept.Count * MeasurementBytes);
        return counts with { Meters = kept.Count == 0 ? null : kept };
    }

    // Every node's lifetime document; the collection holds two documents per node that flushed.
    private static async ValueTask<OpCounts> ReadLifetimeAsync(
        IStorageTransaction transaction, string databaseId, Func<StoredDocument, OpCounts> parse, CancellationToken cancellationToken)
    {
        var lifetime = OpCounts.Zero;
        string? after = null;
        while (true)
        {
            var page = await transaction.ListAsync(UsageCollection(databaseId), after, limit: 100, MaxPageBytes, cancellationToken).ConfigureAwait(false);
            foreach (var document in page.Documents.Where(document => Name(document.Id) == Ops))
            {
                lifetime += parse(document);
            }

            if (!page.HasMore)
            {
                return lifetime;
            }

            after = page.Documents[^1].Id;
        }
    }

    private static async ValueTask<OpCounts[]> ReadSeriesAsync(
        IStorageTransaction transaction, string databaseId, UsageWindow window, Func<StoredDocument, OpCounts> parse, Action? proceed,
        CancellationToken cancellationToken)
    {
        var series = Enumerable.Repeat(OpCounts.Zero, window.Count).ToArray();
        var collection = Collection(databaseId, window.Granularity);
        var after = window.Granularity.Id(window.First - 1);
        var end = window.Granularity.Id(window.First + window.Count);
        while (true)
        {
            proceed?.Invoke();
            var page = await transaction.ListAsync(collection, after, limit: 100, MaxPageBytes, cancellationToken).ConfigureAwait(false);
            foreach (var document in page.Documents)
            {
                if (string.CompareOrdinal(document.Id, end) >= 0)
                {
                    return series;
                }

                var index = window.Granularity.Parse(Name(document.Id)) - window.First;
                // Coarser analytics buckets read finer persisted buckets after the preceding boundary.
                if (index >= 0 && index < series.Length)
                {
                    series[index] += parse(document);
                }
            }

            if (!page.HasMore)
            {
                return series;
            }

            after = page.Documents[^1].Id;
        }
    }

    public void Dispose()
    {
        _flush.Dispose();
        _maps.Dispose();
        DisposeMeasurements();
    }

    private static string UsageCollection(string databaseId) => DatabaseState(databaseId) + "usage";

    // One document month~node per UTC month and node, of units (UsageTotals: reads and writes in operation units).
    private static string ProjectMonths(string projectId) => ProjectState(projectId) + "usage-months";

    private static string OrganizationMonths(string organizationId) => $"organizations/{organizationId}/usage-months";

    private static string MonthId(long hour) => DateTimeOffset.UnixEpoch.AddHours(hour).ToString("yyyy-MM", CultureInfo.InvariantCulture);

    // This node's document of a name.
    private string Own(string name) => name + NodeSeparator + options.MachineId;

    // The name of a document, whichever node wrote it.
    private static string Name(string id) => id.IndexOf(NodeSeparator, StringComparison.Ordinal) is >= 0 and var at ? id[..at] : id;

    private static string Collection(string databaseId, UsageGranularity granularity) => DatabaseState(databaseId) + granularity.Collection;

    private long CurrentMinute() => time.GetUtcNow().ToUnixTimeSeconds() / 60;

    private long CurrentHour() => time.GetUtcNow().ToUnixTimeSeconds() / 3600;

    // Lock-free: retries when a concurrent Add changed the entry; removes it once nothing is pending.
    private void Subtract(UsageKey key, OpCounts counts)
    {
        if (!_pending.TryGetValue(key.Database, out var pending))
        {
            return;
        }

        while (pending.TryGetValue(key, out var current))
        {
            var rest = current - counts;
            if (rest == OpCounts.Zero ? pending.TryRemove(KeyValuePair.Create(key, current)) : pending.TryUpdate(key, rest, current))
            {
                ReleaseMeasurement(key, rest);
                return;
            }
        }
    }
}

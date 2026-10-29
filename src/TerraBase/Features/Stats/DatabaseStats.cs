using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using TerraBase.Hosting;
using TerraBase.Tenancy;

namespace TerraBase.Features.Stats;
internal static class StatsLatency
{
    private static readonly double[] Bounds =
        [0.25, 0.5, 1, 2, 4, 8, 16, 32, 64, 128, 256, 512, 1024, 2048, 4096, 8192, 16384, 32768, 65536];

    internal static IReadOnlyList<double> BoundsMs => Bounds;
    internal static int Bin(TimeSpan elapsed)
    {
        var index = 0;
        while (index < Bounds.Length - 1 && elapsed.TotalMilliseconds > Bounds[index])
        {
            index++;
        }

        return index;
    }
}
internal readonly record struct DatabaseKey(string Organization, string Project, string Database);
internal sealed class DatabaseStats(IMeterFactory meters) : IDatabaseStats
{
    private const int MaxDatabases = 16_384;
    private const int MaxCollections = 65_536;
    private const int MaxCollectionsPerDatabase = 256;
    private static readonly TimeSpan Idle = TimeSpan.FromHours(3);
    private static readonly KeyValuePair<string, object?> KindDatabase = new("kind", "database");
    private static readonly KeyValuePair<string, object?> KindCollection = new("kind", "collection");
    private static readonly IReadOnlyList<CollectionWindow> NoCollections = [];

    private readonly ConcurrentDictionary<DatabaseKey, DatabaseEntry> _databases = new();
    private readonly DatabaseEntry _overflow = new(null);
    private readonly Counter<long> _dropped = meters.Create(Telemetry.MeterName).CreateCounter<long>(
        "terrabase.stats.dropped", "{event}", "Live statistics events refused by a cap.");
    private int _databaseCount;
    private int _collectionCount;
    private DateTimeOffset? _closedAt;
    private long _readsSinceStart;
    private long _writesSinceStart;
    private StatsWindow? _last;
    public StatsWindow? LastWindow => Volatile.Read(ref _last);

    public void Read(TenantContext database)
    {
        var entry = Entry(database);
        Interlocked.Increment(ref entry.Reads);
        entry.Touch();
    }

    public void Wrote(TenantContext database, int writes)
    {
        if (writes <= 0)
        {
            return;
        }

        var entry = Entry(database);
        Interlocked.Add(ref entry.Writes, writes);
        entry.Touch();
    }

    public void Request(TenantContext database, TimeSpan elapsed, bool failed)
    {
        var entry = Entry(database);
        Interlocked.Increment(ref entry.Requests);
        if (failed)
        {
            Interlocked.Increment(ref entry.Errors);
        }

        Interlocked.Increment(ref entry.Latency[StatsLatency.Bin(elapsed)]);
        entry.Touch();
    }

    public void StorageSampled(TenantContext database, long bytes)
    {
        if (bytes < 0)
        {
            return;
        }

        var entry = Entry(database);
        if (ReferenceEquals(entry, _overflow))
        {
            return;
        }

        Volatile.Write(ref entry.StorageBytes, bytes);
        entry.Touch();
    }
    public void CollectionRead(TenantContext database, string collection)
    {
        if (Find(database) is { } entry && Collection(entry, collection) is { } counts)
        {
            Interlocked.Increment(ref counts.Reads);
        }
    }

    public void CollectionWrote(TenantContext database, string collection)
    {
        if (Find(database) is { } entry && Collection(entry, collection) is { } counts)
        {
            Interlocked.Increment(ref counts.Writes);
        }
    }
    internal StatsWindow Close(DateTimeOffset now)
    {
        var duration = _closedAt is { } previous ? now - previous : StatsService.Window;
        _closedAt = now;
        var totals = new Tally();
        var active = new List<DatabaseWindow>();
        var expired = new List<KeyValuePair<DatabaseKey, DatabaseEntry>>();

        foreach (var pair in _databases)
        {
            var entry = pair.Value;
            if (Interlocked.Exchange(ref entry.Touched, 0) != 0)
            {
                entry.LastActive = now;
            }

            var collections = DrainCollections(entry);
            var counters = DrainCounters(entry);
            totals.Add(counters);
            if (IsActive(counters))
            {
                active.Add(new DatabaseWindow(entry.Database!, counters, collections));
            }

            if (now - entry.LastActive > Idle)
            {
                expired.Add(pair);
            }
        }

        foreach (var (key, entry) in expired)
        {
            if (_databases.TryRemove(key, out _))
            {
                Interlocked.Decrement(ref _databaseCount);
                Interlocked.Add(ref _collectionCount, -entry.CollectionCount);
            }
        }

        totals.Add(DrainCounters(_overflow));
        _readsSinceStart += totals.Reads;
        _writesSinceStart += totals.Writes;
        var closed = new StatsWindow(now, duration, _databaseCount, totals.ToCounters(), active,
            _readsSinceStart, _writesSinceStart);
        Volatile.Write(ref _last, closed);
        return closed;
    }


    private static DatabaseKey Key(TenantContext database) => new(database.OrganizationId, database.ProjectId ?? "", database.DatabaseId ?? "");
    private DatabaseEntry Entry(TenantContext database)
    {
        var key = Key(database);
        return _databases.TryGetValue(key, out var entry) ? entry : Add(key, database);
    }

    private DatabaseEntry? Find(TenantContext database) => _databases.TryGetValue(Key(database), out var entry) ? entry : null;
    private DatabaseEntry Add(DatabaseKey key, TenantContext database)
    {
        if (!TryTake(ref _databaseCount, MaxDatabases))
        {
            _dropped.Add(1, KindDatabase);
            return _overflow;
        }

        var created = new DatabaseEntry(database);
        if (_databases.TryAdd(key, created))
        {
            return created;
        }

        Interlocked.Decrement(ref _databaseCount);
        return _databases.TryGetValue(key, out var winner) ? winner : _overflow;
    }
    private CollectionEntry? Collection(DatabaseEntry entry, string name)
    {
        var collections = entry.CollectionsOrCreate();
        if (collections.TryGetValue(name, out var collection))
        {
            return collection;
        }

        if (!TryTake(ref _collectionCount, MaxCollections))
        {
            _dropped.Add(1, KindCollection);
            return null;
        }

        if (!TryTake(ref entry.CollectionCount, MaxCollectionsPerDatabase))
        {
            Interlocked.Decrement(ref _collectionCount);
            _dropped.Add(1, KindCollection);
            return null;
        }

        var created = new CollectionEntry();
        if (collections.TryAdd(name, created))
        {
            return created;
        }

        Interlocked.Decrement(ref _collectionCount);
        Interlocked.Decrement(ref entry.CollectionCount);
        return collections.TryGetValue(name, out var winner) ? winner : null;
    }
    private static bool TryTake(ref int count, int limit)
    {
        if (Interlocked.Increment(ref count) <= limit)
        {
            return true;
        }

        Interlocked.Decrement(ref count);
        return false;
    }

    private IReadOnlyList<CollectionWindow> DrainCollections(DatabaseEntry entry)
    {
        List<CollectionWindow>? collections = null;
        if (entry.Collections is not { } table)
        {
            return NoCollections;
        }

        foreach (var (name, collection) in table)
        {
            var reads = Interlocked.Exchange(ref collection.Reads, 0);
            var writes = Interlocked.Exchange(ref collection.Writes, 0);
            if (reads == 0 && writes == 0)
            {
                if (table.TryRemove(name, out _))
                {
                    Interlocked.Decrement(ref _collectionCount);
                    Interlocked.Decrement(ref entry.CollectionCount);
                }

                continue;
            }

            collections ??= [];
            collections.Add(new CollectionWindow(name, reads, writes));
        }

        return collections ?? NoCollections;
    }

    private static StatsCounters DrainCounters(DatabaseEntry entry)
    {
        long[]? latency = null;
        for (var bin = 0; bin < entry.Latency.Length; bin++)
        {
            var count = Interlocked.Exchange(ref entry.Latency[bin], 0);
            if (count == 0)
            {
                continue;
            }

            latency ??= new long[entry.Latency.Length];
            latency[bin] = count;
        }

        var storage = Volatile.Read(ref entry.StorageBytes);
        return new StatsCounters(Interlocked.Exchange(ref entry.Reads, 0), Interlocked.Exchange(ref entry.Writes, 0),
            Interlocked.Exchange(ref entry.Requests, 0), Interlocked.Exchange(ref entry.Errors, 0), latency, storage < 0 ? null : storage);
    }

    private static bool IsActive(StatsCounters counters) =>
        counters.StorageBytes is not null || counters.Reads != 0 || counters.Writes != 0 || counters.Requests != 0 || counters.Errors != 0;
    private sealed class DatabaseEntry(TenantContext? database)
    {
        internal TenantContext? Database { get; } = database;
        internal long Reads;
        internal long Writes;
        internal long Requests;
        internal long Errors;
        internal readonly long[] Latency = new long[StatsLatency.BoundsMs.Count];
        internal long StorageBytes = -1;
        internal int Touched = 1;
        internal DateTimeOffset LastActive;
        internal int CollectionCount;
        private ConcurrentDictionary<string, CollectionEntry>? _collections;


        internal ConcurrentDictionary<string, CollectionEntry>? Collections => Volatile.Read(ref _collections);


        internal ConcurrentDictionary<string, CollectionEntry> CollectionsOrCreate() => Create(ref _collections);

        internal void Touch() => Volatile.Write(ref Touched, 1);
        private static TTable Create<TTable>(ref TTable? table) where TTable : class, new()
        {
            var existing = Volatile.Read(ref table);
            if (existing is not null)
            {
                return existing;
            }

            var created = new TTable();
            return Interlocked.CompareExchange(ref table, created, null) ?? created;
        }
    }

    private sealed class CollectionEntry
    {
        internal long Reads;
        internal long Writes;
    }

    private sealed class Tally
    {
        internal long Reads;
        internal long Writes;
        internal long Requests;
        internal long Errors;
        internal long? StorageBytes;
        internal readonly long[] Latency = new long[StatsLatency.BoundsMs.Count];

        internal void Add(StatsCounters counters)
        {
            Reads += counters.Reads;
            Writes += counters.Writes;
            Requests += counters.Requests;
            Errors += counters.Errors;
            StorageBytes = counters.StorageBytes is { } storage ? (StorageBytes ?? 0) + storage : StorageBytes;
            if (counters.Latency is { } bins)
            {
                for (var bin = 0; bin < Latency.Length; bin++)
                {
                    Latency[bin] += bins[bin];
                }
            }
        }

        internal StatsCounters ToCounters() =>
            new(Reads, Writes, Requests, Errors, Array.Exists(Latency, count => count != 0) ? Latency : null, StorageBytes);
    }
}

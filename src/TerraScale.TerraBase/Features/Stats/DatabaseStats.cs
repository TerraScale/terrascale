using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using TerraBase.Hosting;
using TerraBase.Tenancy;

namespace TerraBase.Features.Stats;

/// <summary>Upper bounds, in milliseconds, of the request latency bins. The last bin includes longer requests.</summary>
internal static class StatsLatency
{
    private static readonly double[] Bounds =
        [0.25, 0.5, 1, 2, 4, 8, 16, 32, 64, 128, 256, 512, 1024, 2048, 4096, 8192, 16384, 32768, 65536];

    internal static IReadOnlyList<double> BoundsMs => Bounds;

    // The first bin whose bound holds the request; the last bin takes every longer one.
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

/// <summary>A database by its catalog IDs. Its plan is not part of the key.</summary>
internal readonly record struct DatabaseKey(string Organization, string Project, string Database);

/// <summary>
/// Live counters per database, peer region and collection. Hot-path calls only add to atomics, and find existing entries
/// with <c>TryGetValue</c>. <see cref="Close"/> swaps the window counters for zero, evicts idle databases and publishes the
/// closed window as <see cref="LastWindow"/>. Every table is capped; a refused event still counts in the database totals and
/// in <c>terrabase.stats.dropped</c>. Callers validate peer regions first.
/// </summary>
internal sealed class DatabaseStats(IMeterFactory meters) : IDatabaseStats
{
    private const int MaxDatabases = 16_384;
    private const int MaxPeers = 65_536;
    private const int MaxCollections = 65_536;
    private const int MaxRegions = 64;
    private const int MaxPeersPerDatabase = 64;
    private const int MaxCollectionsPerDatabase = 256;
    // A database idle for this long, with no storage sample, is evicted.
    private static readonly TimeSpan Idle = TimeSpan.FromHours(3);
    private static readonly KeyValuePair<string, object?> KindDatabase = new("kind", "database");
    private static readonly KeyValuePair<string, object?> KindPeer = new("kind", "peer");
    private static readonly KeyValuePair<string, object?> KindCollection = new("kind", "collection");
    // Shared by every window with nothing to report, so an idle database allocates no list.
    private static readonly IReadOnlyList<PeerCounters> NoPeers = [];
    private static readonly IReadOnlyList<CollectionWindow> NoCollections = [];

    private readonly ConcurrentDictionary<DatabaseKey, DatabaseEntry> _databases = new();
    private readonly ConcurrentDictionary<string, RegionEntry> _regions = new(StringComparer.Ordinal);
    // Stands in for a database the full table refused: its events count in the totals, and it is never listed.
    private readonly DatabaseEntry _overflow = new(null);
    private readonly Counter<long> _dropped = meters.Create(Telemetry.MeterName).CreateCounter<long>(
        "terrabase.stats.dropped", "{event}", "Live statistics events refused by a cap.");
    private int _databaseCount;
    private int _regionCount;
    private int _peerCount;
    private int _collectionCount;
    // Written only by Close.
    private DateTimeOffset? _closedAt;
    private long _readsSinceStart;
    private long _writesSinceStart;
    private StatsWindow? _last;

    /// <summary>The most recently closed window, or null before the first close.</summary>
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

    public void ReplicationBytes(TenantContext database, string peerRegion, bool sent, long bytes)
    {
        if (bytes <= 0)
        {
            return;
        }

        var entry = Entry(database);
        AddBytes(entry, sent, bytes);
        var region = Region(peerRegion);
        if (region is not null)
        {
            AddBytes(region, sent, bytes);
        }

        if (Peer(entry, peerRegion) is { } peer)
        {
            AddBytes(peer, sent, bytes);
        }

        entry.Touch();
    }

    public void ReplicationState(TenantContext database, string peerRegion, long remaining, double lagMs, bool diverged)
    {
        var entry = Entry(database);
        entry.Touch();
        if (Peer(entry, peerRegion) is not { } peer)
        {
            return;
        }

        Volatile.Write(ref peer.Remaining, remaining);
        Volatile.Write(ref peer.LagBits, BitConverter.DoubleToInt64Bits(lagMs));
        Volatile.Write(ref peer.Diverged, diverged ? 1 : 0);
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

    // Collections are counted only for a database already tracked: its database-level event has counted any refusal.
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

    /// <summary>Closes the window that ends at <paramref name="now"/>. Only <see cref="StatsService"/> calls this, from one thread.</summary>
    internal void Close(DateTimeOffset now)
    {
        var duration = _closedAt is { } previous ? now - previous : StatsService.Window;
        _closedAt = now;
        var totals = new Tally();
        var active = new List<DatabaseWindow>();
        var expired = new List<KeyValuePair<DatabaseKey, DatabaseEntry>>();
        var regionTallies = new Dictionary<string, PeerTally>(StringComparer.Ordinal);

        foreach (var pair in _databases)
        {
            var entry = pair.Value;
            if (Interlocked.Exchange(ref entry.Touched, 0) != 0)
            {
                entry.LastActive = now;
            }

            var peers = DrainPeers(entry);
            var collections = DrainCollections(entry);
            var counters = DrainCounters(entry, peers);
            totals.Add(counters);
            foreach (var peer in peers)
            {
                var tally = PeerTallyOf(regionTallies, peer.Region);
                tally.LagGroups += peer.LagGroups;
                tally.LagSeconds = Math.Max(tally.LagSeconds, peer.LagSeconds);
                tally.Conflicts += peer.Conflicts;
            }

            if (IsActive(counters))
            {
                active.Add(new DatabaseWindow(entry.Database!, counters, peers, collections));
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
                Interlocked.Add(ref _peerCount, -entry.PeerCount);
                Interlocked.Add(ref _collectionCount, -entry.CollectionCount);
            }
        }

        totals.Add(DrainCounters(_overflow, []));
        var memberPeers = new List<PeerCounters>();
        var cumulative = new List<RegionTotal>();
        foreach (var (region, entry) in _regions)
        {
            var sent = Interlocked.Exchange(ref entry.SentBytes, 0);
            var received = Interlocked.Exchange(ref entry.ReceivedBytes, 0);
            entry.SentTotal += sent;
            entry.ReceivedTotal += received;
            var tally = PeerTallyOf(regionTallies, region);
            tally.SentBytes += sent;
            tally.ReceivedBytes += received;
            cumulative.Add(new RegionTotal(region, entry.SentTotal, entry.ReceivedTotal));
        }

        foreach (var (region, tally) in regionTallies)
        {
            if (!tally.IsZero)
            {
                memberPeers.Add(tally.ToCounters(region));
            }
        }

        _readsSinceStart += totals.Reads;
        _writesSinceStart += totals.Writes;
        Volatile.Write(ref _last, new StatsWindow(now, duration, _databaseCount, totals.ToCounters(), memberPeers, active, cumulative,
            _readsSinceStart, _writesSinceStart));
    }

    private static void AddBytes(ByteCounters counters, bool sent, long bytes) =>
        Interlocked.Add(ref sent ? ref counters.SentBytes : ref counters.ReceivedBytes, bytes);

    private static DatabaseKey Key(TenantContext database) => new(database.OrganizationId, database.ProjectId ?? "", database.DatabaseId ?? "");

    // Hot path: a tracked database is found without allocating. A new database or an overflow takes the miss path below.
    private DatabaseEntry Entry(TenantContext database)
    {
        var key = Key(database);
        return _databases.TryGetValue(key, out var entry) ? entry : Add(key, database);
    }

    private DatabaseEntry? Find(TenantContext database) => _databases.TryGetValue(Key(database), out var entry) ? entry : null;

    // Runs only on a miss. A refused database counts against the overflow, which is never tracked.
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

    // Null for the overflow and when the peer caps refuse the peer; the database counters still count.
    private PeerEntry? Peer(DatabaseEntry entry, string region)
    {
        if (ReferenceEquals(entry, _overflow))
        {
            return null;
        }

        var peers = entry.PeersOrCreate();
        if (peers.TryGetValue(region, out var peer))
        {
            return peer;
        }

        if (!TryTake(ref _peerCount, MaxPeers))
        {
            _dropped.Add(1, KindPeer);
            return null;
        }

        if (!TryTake(ref entry.PeerCount, MaxPeersPerDatabase))
        {
            Interlocked.Decrement(ref _peerCount);
            _dropped.Add(1, KindPeer);
            return null;
        }

        var created = new PeerEntry();
        if (peers.TryAdd(region, created))
        {
            return created;
        }

        Interlocked.Decrement(ref _peerCount);
        Interlocked.Decrement(ref entry.PeerCount);
        return peers.TryGetValue(region, out var winner) ? winner : null;
    }

    // Null when the collection caps refuse it; the database counters still count the event.
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

    // Replication totals per region are kept for the whole member, so no database's eviction takes them away.
    private RegionEntry? Region(string region)
    {
        if (_regions.TryGetValue(region, out var found))
        {
            return found;
        }

        if (!TryTake(ref _regionCount, MaxRegions))
        {
            _dropped.Add(1, KindPeer);
            return null;
        }

        var created = new RegionEntry();
        if (_regions.TryAdd(region, created))
        {
            return created;
        }

        Interlocked.Decrement(ref _regionCount);
        return _regions.TryGetValue(region, out var winner) ? winner : null;
    }

    // Takes one slot of a count capped at limit; nothing is taken when it is full.
    private static bool TryTake(ref int count, int limit)
    {
        if (Interlocked.Increment(ref count) <= limit)
        {
            return true;
        }

        Interlocked.Decrement(ref count);
        return false;
    }

    private static PeerTally PeerTallyOf(Dictionary<string, PeerTally> tallies, string region)
    {
        if (!tallies.TryGetValue(region, out var tally))
        {
            tally = new PeerTally();
            tallies[region] = tally;
        }

        return tally;
    }

    // Exchanges the window's peer counters for zero and reads its gauges; idle peers are omitted.
    private static IReadOnlyList<PeerCounters> DrainPeers(DatabaseEntry entry)
    {
        List<PeerCounters>? peers = null;
        if (entry.Peers is not { } table)
        {
            return NoPeers;
        }

        foreach (var (region, peer) in table)
        {
            var sent = Interlocked.Exchange(ref peer.SentBytes, 0);
            var received = Interlocked.Exchange(ref peer.ReceivedBytes, 0);
            var remaining = Volatile.Read(ref peer.Remaining);
            var lag = BitConverter.Int64BitsToDouble(Volatile.Read(ref peer.LagBits)) / 1000;
            var diverged = Volatile.Read(ref peer.Diverged) != 0;
            if (sent == 0 && received == 0 && remaining == 0 && lag == 0 && !diverged)
            {
                continue;
            }

            peers ??= [];
            peers.Add(new PeerCounters(region, sent, received, remaining, lag, diverged ? 1 : 0));
        }

        return peers ?? NoPeers;
    }

    // Exchanges the window's collection counters for zero. An idle collection gives its slot back; a write racing this is lost.
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

    private static StatsCounters DrainCounters(DatabaseEntry entry, IReadOnlyList<PeerCounters> peers)
    {
        var lagGroups = 0L;
        var lagSeconds = 0d;
        var conflicts = 0L;
        foreach (var peer in peers)
        {
            lagGroups += peer.LagGroups;
            lagSeconds = Math.Max(lagSeconds, peer.LagSeconds);
            conflicts += peer.Conflicts;
        }

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
            Interlocked.Exchange(ref entry.Requests, 0), Interlocked.Exchange(ref entry.Errors, 0), Interlocked.Exchange(ref entry.SentBytes, 0),
            Interlocked.Exchange(ref entry.ReceivedBytes, 0), lagGroups, lagSeconds, conflicts, latency, storage < 0 ? null : storage);
    }

    private static bool IsActive(StatsCounters counters) =>
        counters.StorageBytes is not null || counters.Reads != 0 || counters.Writes != 0 || counters.Requests != 0 || counters.Errors != 0
        || counters.SentBytes != 0 || counters.ReceivedBytes != 0 || counters.LagGroups != 0 || counters.LagSeconds != 0 || counters.Conflicts != 0;

    // Bytes a region, a peer or a database moved in the window and since start.
    private class ByteCounters
    {
        internal long SentBytes;
        internal long ReceivedBytes;
    }

    private sealed class DatabaseEntry(TenantContext? database) : ByteCounters
    {
        // Null only for the overflow.
        internal TenantContext? Database { get; } = database;
        internal long Reads;
        internal long Writes;
        internal long Requests;
        internal long Errors;
        internal readonly long[] Latency = new long[StatsLatency.BoundsMs.Count];
        // A gauge: -1 until the first storage sample.
        internal long StorageBytes = -1;
        // Starts set so a Close racing the creator's first Touch does not evict the new entry.
        internal int Touched = 1;
        // Set by Close only: the last window with activity or a storage sample.
        internal DateTimeOffset LastActive;
        internal int PeerCount;
        internal int CollectionCount;
        private ConcurrentDictionary<string, PeerEntry>? _peers;
        private ConcurrentDictionary<string, CollectionEntry>? _collections;

        internal ConcurrentDictionary<string, PeerEntry>? Peers => Volatile.Read(ref _peers);

        internal ConcurrentDictionary<string, CollectionEntry>? Collections => Volatile.Read(ref _collections);

        internal ConcurrentDictionary<string, PeerEntry> PeersOrCreate() => Create(ref _peers);

        internal ConcurrentDictionary<string, CollectionEntry> CollectionsOrCreate() => Create(ref _collections);

        internal void Touch() => Volatile.Write(ref Touched, 1);

        // Tables are created on first use: most databases have no peer or collection breakdown. Default string comparison is ordinal.
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

    private sealed class PeerEntry : ByteCounters
    {
        internal long Remaining;
        internal long LagBits;
        internal int Diverged;
    }

    private sealed class CollectionEntry
    {
        internal long Reads;
        internal long Writes;
    }

    private sealed class RegionEntry : ByteCounters
    {
        // Close only: bytes since start.
        internal long SentTotal;
        internal long ReceivedTotal;
    }

    // Sums counters of many databases into one member total.
    private sealed class Tally
    {
        internal long Reads;
        internal long Writes;
        internal long Requests;
        internal long Errors;
        internal long SentBytes;
        internal long ReceivedBytes;
        internal long LagGroups;
        internal double LagSeconds;
        internal long Conflicts;
        internal long? StorageBytes;
        internal readonly long[] Latency = new long[StatsLatency.BoundsMs.Count];

        internal void Add(StatsCounters counters)
        {
            Reads += counters.Reads;
            Writes += counters.Writes;
            Requests += counters.Requests;
            Errors += counters.Errors;
            SentBytes += counters.SentBytes;
            ReceivedBytes += counters.ReceivedBytes;
            LagGroups += counters.LagGroups;
            LagSeconds = Math.Max(LagSeconds, counters.LagSeconds);
            Conflicts += counters.Conflicts;
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
            new(Reads, Writes, Requests, Errors, SentBytes, ReceivedBytes, LagGroups, LagSeconds, Conflicts,
                Array.Exists(Latency, count => count != 0) ? Latency : null, StorageBytes);
    }

    // A member's view of one peer region: bytes from the region totals, lag and divergence summed over its databases.
    private sealed class PeerTally
    {
        internal long SentBytes;
        internal long ReceivedBytes;
        internal long LagGroups;
        internal double LagSeconds;
        internal long Conflicts;

        internal bool IsZero => SentBytes == 0 && ReceivedBytes == 0 && LagGroups == 0 && LagSeconds == 0 && Conflicts == 0;

        internal PeerCounters ToCounters(string region) => new(region, SentBytes, ReceivedBytes, LagGroups, LagSeconds, Conflicts);
    }
}

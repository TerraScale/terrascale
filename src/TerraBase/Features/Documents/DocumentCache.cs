using System.Diagnostics.Metrics;
using System.Text.Json;
using System.Text.Json.Nodes;
using TerraBase.Configuration;
using TerraBase.Engine;
using TerraBase.Features.Memory;
using TerraBase.Features.Qos;
using TerraBase.Hosting;
using TerraBase.Tenancy;

namespace TerraBase.Features.Documents;

/// <summary>
/// Read-through cache of documents and list pages. Each entry keeps the <see cref="IWriteStamps"/> stamp taken
/// before its storage read and is served only while that stamp is unchanged, so no read returns a version older
/// than a write this node acknowledged. Tenant CLOCK rings share a 4096-entry and quarter-memory-budget bound.
/// Each organization evicts only its own snapshots; every entry is also charged to <see cref="IMemoryBudget"/>.
/// </summary>
internal sealed class DocumentCache : IDocumentCacheQuota, IDisposable
{
    // Rough per-entry overhead: entry, key, value objects and the cache's own bookkeeping.
    private const long EntryOverhead = 256;
    private const int EntryLimit = 4096;

    // Rough per-document overhead beyond its JSON and id characters: the StoredDocument, array and string
    // headers, and its slot in a page's list.
    private const long DocumentOverhead = 96;

    private readonly IMemoryBudget _budget;
    private readonly IWriteStamps _stamps;
    private readonly long _sizeLimit;
    private readonly Dictionary<object, LinkedListNode<Entry>> _entries = [];
    private readonly Dictionary<string, Partition> _partitions = [];
    private readonly Counter<long> _lookups;
    private readonly Counter<long> _promotions;
    private readonly Counter<long> _evictions;
    // ponytail: one gate includes retained copies; split admission only if measured contention warrants it.
    private readonly Lock _gate = new();
    private long _bytes;
    private int _entryCount;
    private bool _disposed;

    public DocumentCache(TerraBaseOptions options, IMemoryBudget budget, IWriteStamps stamps, IMeterFactory meters)
    {
        _budget = budget;
        _stamps = stamps;
        _sizeLimit = options.MemoryBudget / 4;
        var meter = meters.Create(Telemetry.MeterName);
        _lookups = meter.CreateCounter<long>(
            "terrabase.document_cache.lookups", description: "Document and list-page cache lookups by cache and result (hit, miss).");
        _promotions = meter.CreateCounter<long>(
            "terrabase.document_cache.promotions", description: "Clean snapshots admitted to the memory cache on storage reads.");
        _evictions = meter.CreateCounter<long>(
            "terrabase.document_cache.evictions", description: "Clean snapshots removed by reason (capacity, stale, disposed).");
        meter.CreateObservableGauge("terrabase.document_cache.bytes", () => Volatile.Read(ref _bytes), "By",
            "Estimated retained bytes charged to memory-budget.");
        meter.CreateObservableGauge("terrabase.document_cache.entries", () => Volatile.Read(ref _entryCount),
            description: "Retained clean snapshots across documents, system documents and query pages.");
    }

    /// <summary>Releases this organization's old-plan cache excess before request memory admission.</summary>
    public void Reconcile(TenantContext tenant)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        var share = QosPolicy.For(tenant.Plan).CacheShare;
        lock (_gate)
        {
            Trim(tenant.OrganizationId, (long)(_sizeLimit * share), Math.Max(1, (int)(EntryLimit * share)));
        }
    }

    public ValueTask<StoredDocument?> GetAsync(DocumentKey key, Func<ValueTask<StoredDocument?>> read) =>
        GetAsync(key, key.Database, () => _stamps.Document(key.Database, key.Collection, key.Id), "document", read,
            document => Size(document) + TenantSize(key.Database) + Chars(key.Collection) + Chars(key.Id), Copy);

    public ValueTask<StoredDocument?> GetSystemAsync(string collection, string id, Func<ValueTask<StoredDocument?>> read) =>
        GetAsync((collection, id), null, () => _stamps.Document(null, collection, id), "document", read,
            document => Size(document) + Chars(collection) + Chars(id), Copy);

    public async ValueTask<CachedQuery> QueryAsync(
        TenantContext database, DocumentQuery query, long? countLimit, Func<ValueTask<CachedQuery>> read)
    {
        var key = new QueryKey(database, query.Collection, Normalize(query, countLimit));
        return (await GetAsync(key, database, () => _stamps.Collection(database, query.Collection), "page",
            async () => (CachedQuery?)await read().ConfigureAwait(false),
            result => result.Page.Rows.Sum(row => Size(row.Document) + DocumentOverhead
                + row.Keys.Sum(value => value is string text ? Chars(text) : sizeof(long)))
                + TenantSize(database) + Chars(key.Collection) + Chars(key.Query), Copy).ConfigureAwait(false))!;
    }

    private static CachedQuery Copy(CachedQuery result) => result with
    {
        Page = result.Page with
        {
            Rows = Array.AsReadOnly(result.Page.Rows.Select(row => row with
            {
                Document = Copy(row.Document),
                Keys = Array.AsReadOnly(row.Keys.ToArray()),
            }).ToArray()),
        },
    };

    private static StoredDocument Copy(StoredDocument document) => document with { Json = document.Json.ToArray() };

    // JSON arrays encode decoded paths, typed filter nodes and every result-shaping parameter without
    // delimiter ambiguity. Projection happens after the cache, so different projections share raw pages.
    private static string Normalize(DocumentQuery query, long? countLimit)
    {
        var normalized = new JsonObject
        {
            ["filter"] = Filter(query.Filter),
            ["sort"] = new JsonArray([.. query.Sort.Select(key => (JsonNode)new JsonArray(Path(key.Path), JsonValue.Create(key.Descending)))]),
            ["after"] = query.After is { } after ? new JsonArray(
                JsonValue.Create(after.Id), new JsonArray([.. after.Keys.Select(Scalar)])) : null,
            ["limit"] = query.Limit,
            ["maxBytes"] = query.MaxBytes,
            ["countLimit"] = countLimit,
        };
        return JsonSerializer.Serialize(normalized, TerraBaseJsonContext.Default.JsonObject);
    }

    private static JsonArray? Path(DocumentPath path) => path.Segments is { } segments
        ? [.. segments.Select(segment => (JsonNode?)JsonValue.Create(segment))] : null;

    private static JsonNode? Scalar(object? value) => value switch
    {
        null => null,
        string text => JsonValue.Create(text),
        bool boolean => JsonValue.Create(boolean),
        long integer => JsonValue.Create(integer),
        double number => JsonValue.Create(number),
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private static JsonArray Value(ScalarValue value) => new(JsonValue.Create((int)value.Kind), Scalar(value.Value));

    private static JsonNode? Filter(DocumentFilter? filter) => filter switch
    {
        null => null,
        AndFilter and => new JsonArray(JsonValue.Create("and"), new JsonArray([.. and.Children.Select(Filter)])),
        OrFilter or => new JsonArray(JsonValue.Create("or"), new JsonArray([.. or.Children.Select(Filter)])),
        NotFilter not => new JsonArray(JsonValue.Create("not"), Filter(not.Child)),
        CompareFilter compare => new JsonArray(JsonValue.Create("compare"), Path(compare.Path), JsonValue.Create((int)compare.Op), Value(compare.Value)),
        InFilter included => new JsonArray(JsonValue.Create("in"), Path(included.Path), new JsonArray([.. included.Values.Select(value => (JsonNode?)Value(value))])),
        ExistsFilter exists => new JsonArray(JsonValue.Create("exists"), Path(exists.Path)),
        PrefixFilter prefix => new JsonArray(JsonValue.Create("prefix"), Path(prefix.Path), JsonValue.Create(prefix.Prefix)),
        ContainsFilter contains => new JsonArray(JsonValue.Create("contains"), Path(contains.Path), Value(contains.Value)),
        _ => throw new ArgumentOutOfRangeException(nameof(filter)),
    };

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            while (_entries.Count > 0)
            {
                Remove(_entries.Values.First(), "disposed");
            }
        }
    }

    private async ValueTask<T?> GetAsync<T>(object key, TenantContext? tenant, Func<long> currentStamp, string cache, Func<ValueTask<T?>> read, Func<T, long> size, Func<T, T> copy)
        where T : class
    {
        // The request's current plan wins over a database handle created before a plan change.
        if (tenant is not null && QosContext.Current is { } current && current.OrganizationId == tenant.OrganizationId)
        {
            tenant = current;
        }
        var organization = tenant?.OrganizationId ?? string.Empty;
        var share = tenant is null ? 1 : QosPolicy.For(tenant.Plan).CacheShare;
        var byteLimit = (long)(_sizeLimit * share);
        var countLimit = Math.Max(1, (int)(EntryLimit * share));
        var stamp = currentStamp();
        T? hit = null;
        lock (_gate)
        {
            Trim(organization, byteLimit, countLimit);
            if (!_disposed && _entries.TryGetValue(key, out var node))
            {
                var cached = node.Value;
                if (cached.Stamp == stamp)
                {
                    cached.Referenced = true;
                    hit = (T)cached.Value;
                }

                else if (cached.Stamp < stamp)
                {
                    // Evict older generations only: a delayed lookup must preserve a newer warmed entry.
                    Remove(node, "stale");
                }
            }
        }

        if (hit is not null)
        {
            _lookups.Add(1, new("cache", cache), new("result", "hit"));
            QosContext.ReserveResult(checked(size(hit) * 3 + EntryOverhead), tenant);
            return copy(hit);
        }

        _lookups.Add(1, new("cache", cache), new("result", "miss"));
        // Absent documents and databases are not cached.
        if (await read().ConfigureAwait(false) is not { } value)
        {
            return null;
        }

        var bytes = size(value) + EntryOverhead;
        // Oversized reads bypass admission without displacing useful snapshots.
        if (bytes > byteLimit || currentStamp() != stamp)
        {
            return value;
        }

        lock (_gate)
        {
            // Delayed reads must neither replace nor displace a newer warmed entry.
            if (_disposed || currentStamp() != stamp
                || (_entries.TryGetValue(key, out var existing) && existing.Value.Stamp >= stamp))
            {
                return value;
            }

            if (existing is not null)
            {
                Remove(existing, "stale");
            }

            _partitions.TryGetValue(organization, out var partition);
            if (partition is not null)
            {
                while (partition.Clock.Count > 0
                    && (partition.Clock.Count >= countLimit || bytes > byteLimit - partition.Bytes
                        || _entries.Count >= EntryLimit || bytes > _sizeLimit - _bytes))
                {
                    Evict(partition);
                }
            }

            // Global pressure never displaces another organization's retained snapshots.
            if (_entries.Count >= EntryLimit || bytes > _sizeLimit - _bytes)
            {
                return value;
            }

            if (_budget.TryReserve(MemoryLane.Foreground, bytes, tenant) is not { } reservation)
            {
                return value;
            }

            var published = false;
            try
            {
                // Reserve before retaining a detached copy; callers never receive cache-owned buffers.
                var retained = copy(value);
                if (currentStamp() != stamp)
                {
                    return value;
                }

                var entry = new LinkedListNode<Entry>(new Entry(key, organization, retained, stamp, bytes, reservation));
                _entries.Add(key, entry);
                partition ??= new Partition();
                _partitions[organization] = partition;
                partition.Clock.AddLast(entry);
                partition.Hand ??= entry;
                partition.Bytes += bytes;
                _bytes += bytes;
                _entryCount = _entries.Count;
                published = true;
                _promotions.Add(1);
            }
            finally
            {
                if (!published)
                {
                    reservation.Dispose();
                }
            }
        }

        return value;
    }

    private void Evict(Partition partition)
    {
        // Every retained entry is a clean storage snapshot, so no dirty state needs flushing or skipping.
        while (partition.Hand is { } entry)
        {
            partition.Hand = entry.Next ?? partition.Clock.First;
            if (entry.Value.Referenced)
            {
                entry.Value.Referenced = false;
            }
            else
            {
                Remove(entry, "capacity");
                return;
            }
        }
    }

    private void Trim(string organization, long byteLimit, int countLimit)
    {
        if (_partitions.TryGetValue(organization, out var partition))
        {
            while (partition.Clock.Count > countLimit || partition.Bytes > byteLimit)
            {
                Evict(partition);
            }
        }
    }

    private void Remove(LinkedListNode<Entry> entry, string reason)
    {
        var partition = _partitions[entry.Value.Organization];
        if (partition.Hand == entry)
        {
            partition.Hand = entry.Next ?? partition.Clock.First;
            if (partition.Hand == entry)
            {
                partition.Hand = null;
            }
        }

        partition.Clock.Remove(entry);
        partition.Bytes -= entry.Value.Bytes;
        if (partition.Clock.Count == 0)
        {
            _partitions.Remove(entry.Value.Organization);
        }
        _entries.Remove(entry.Value.Key);
        _bytes -= entry.Value.Bytes;
        _entryCount = _entries.Count;
        // Release synchronously, before another admission or Dispose can finish.
        entry.Value.Reservation.Dispose();
        _evictions.Add(1, new KeyValuePair<string, object?>("reason", reason));
    }

    // Empty partitions are removed, so their count is bounded by the global entry limit.
    private sealed class Partition
    {
        public LinkedList<Entry> Clock { get; } = new();
        public LinkedListNode<Entry>? Hand { get; set; }
        public long Bytes { get; set; }
    }

    private static long Size(StoredDocument document) => DocumentOverhead + document.Json.Length + Chars(document.Id);

    private static long TenantSize(TenantContext database) =>
        Chars(database.OrganizationId) + Chars(database.ProjectId) + Chars(database.DatabaseId);

    private static long Chars(string? value) => (value?.Length ?? 0) * sizeof(char);

    private sealed record Entry(object Key, string Organization, object Value, long Stamp, long Bytes, IMemoryReservation Reservation)
    {
        // Admission stays read-through; a subsequent hit earns a CLOCK second chance.
        public bool Referenced { get; set; }
    }
}

/// <summary>A normalized query over raw documents, including its optional bounded count.</summary>
internal sealed record QueryKey(TenantContext Database, string Collection, string Query);

/// <summary>A page and optional count read from the same storage transaction.</summary>
internal sealed record CachedQuery(QueryPage Page, (long Count, bool Complete)? Count);

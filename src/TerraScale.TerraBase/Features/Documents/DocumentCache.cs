using System.Diagnostics.Metrics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Caching.Memory;
using TerraScale.TerraBase.Configuration;
using TerraScale.TerraBase.Engine;
using TerraScale.TerraBase.Features.Memory;
using TerraScale.TerraBase.Hosting;
using TerraScale.TerraBase.Tenancy;

namespace TerraScale.TerraBase.Features.Documents;

/// <summary>
/// Read-through cache of documents and list pages. Each entry keeps the <see cref="IWriteStamps"/> stamp taken
/// before its storage read and is served only while that stamp is unchanged, so no read returns a version older
/// than a write this node acknowledged. Bounded to a quarter of <c>memory-budget</c>; every entry is also charged
/// to <see cref="IMemoryBudget"/>.
/// </summary>
internal sealed class DocumentCache : IDisposable
{
    // Rough per-entry overhead: entry, key, value objects and the cache's own bookkeeping.
    private const long EntryOverhead = 256;

    // Rough per-document overhead beyond its JSON and id characters: the StoredDocument, array and string
    // headers, and its slot in a page's list.
    private const long DocumentOverhead = 96;

    private readonly IMemoryBudget _budget;
    private readonly IWriteStamps _stamps;
    private readonly long _sizeLimit;
    private readonly MemoryCache _cache;
    private readonly Counter<long> _lookups;
    // ponytail: one gate for cache operations; split it only if measured contention warrants it.
    private readonly Lock _gate = new();
    private bool _disposed;

    public DocumentCache(TerraBaseOptions options, IMemoryBudget budget, IWriteStamps stamps, IMeterFactory meters)
    {
        _budget = budget;
        _stamps = stamps;
        _sizeLimit = options.MemoryBudget / 4;
        _cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = _sizeLimit });
        _lookups = meters.Create(Telemetry.MeterName).CreateCounter<long>(
            "terrabase.document_cache.lookups", description: "Document and list-page cache lookups by cache and result (hit, miss).");
    }

    public ValueTask<StoredDocument?> GetAsync(DocumentKey key, Func<ValueTask<StoredDocument?>> read) =>
        GetAsync(key, () => _stamps.Document(key.Database, key.Collection, key.Id), "document", read,
            document => Size(document) + TenantSize(key.Database) + Chars(key.Collection) + Chars(key.Id), Copy);

    public ValueTask<StoredDocument?> GetSystemAsync(string collection, string id, Func<ValueTask<StoredDocument?>> read) =>
        GetAsync((collection, id), () => _stamps.Document(null, collection, id), "document", read,
            document => Size(document) + Chars(collection) + Chars(id), Copy);

    public async ValueTask<CachedQuery> QueryAsync(
        TenantContext database, DocumentQuery query, long? countLimit, Func<ValueTask<CachedQuery>> read)
    {
        var key = new QueryKey(database, query.Collection, Normalize(query, countLimit));
        return (await GetAsync(key, () => _stamps.Collection(database, query.Collection), "page",
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
            // Clear evicts every entry, so each returns its reservation to the budget; Dispose alone would not.
            _cache.Clear();
            _cache.Dispose();
        }
    }

    private async ValueTask<T?> GetAsync<T>(object key, Func<long> currentStamp, string cache, Func<ValueTask<T?>> read, Func<T, long> size, Func<T, T> copy)
        where T : class
    {
        var stamp = currentStamp();
        T? hit = null;
        lock (_gate)
        {
            if (!_disposed && _cache.TryGetValue(key, out Entry? cached))
            {
                if (cached!.Stamp == stamp)
                {
                    hit = (T)cached.Value;
                }

                else if (cached.Stamp < stamp)
                {
                    // Evict older generations only: a delayed lookup must preserve a newer warmed entry.
                    _cache.Remove(key);
                }
            }
        }

        if (hit is not null)
        {
            _lookups.Add(1, new("cache", cache), new("result", "hit"));
            return copy(hit);
        }

        _lookups.Add(1, new("cache", cache), new("result", "miss"));
        // Absent documents and databases are not cached.
        if (await read().ConfigureAwait(false) is not { } value)
        {
            return null;
        }

        var bytes = size(value) + EntryOverhead;
        // Reject before admission: MemoryCache would refuse it and compact useful entries anyway.
        if (bytes > _sizeLimit || currentStamp() != stamp)
        {
            return value;
        }

        if (_budget.TryReserve(MemoryLane.Foreground, bytes) is { } reservation)
        {
            var published = false;
            try
            {
                // Reserve before retaining a detached copy; callers never receive cache-owned buffers.
                var retained = copy(value);
                lock (_gate)
                {
                    // Reads in flight may finish after disposal, but cannot publish another entry.
                    if (!_disposed && currentStamp() == stamp
                        && (!_cache.TryGetValue(key, out Entry? existing) || existing!.Stamp < stamp))
                    {
                        using (var entry = _cache.CreateEntry(key))
                        {
                            entry.Value = new Entry(retained, stamp, reservation);
                            entry.Size = bytes;
                            entry.RegisterPostEvictionCallback(static (_, evicted, _, _) => ((Entry)evicted!).Reservation.Dispose());
                        }
                        published = true;
                    }
                }
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

    private static long Size(StoredDocument document) => DocumentOverhead + document.Json.Length + Chars(document.Id);

    private static long TenantSize(TenantContext database) =>
        Chars(database.OrganizationId) + Chars(database.ProjectId) + Chars(database.DatabaseId);

    private static long Chars(string? value) => (value?.Length ?? 0) * sizeof(char);

    private sealed record Entry(object Value, long Stamp, IMemoryReservation Reservation);
}

/// <summary>A normalized query over raw documents, including its optional bounded count.</summary>
internal sealed record QueryKey(TenantContext Database, string Collection, string Query);

/// <summary>A page and optional count read from the same storage transaction.</summary>
internal sealed record CachedQuery(QueryPage Page, (long Count, bool Complete)? Count);

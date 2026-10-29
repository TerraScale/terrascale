using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TerraBase.Engine;
using TerraBase.Errors;
using TerraBase.Features.Memory;
using TerraBase.Hosting;
using TerraBase.Primitives;
using TerraBase.Tenancy;
using static TerraBase.Features.Management.SystemCatalog;

namespace TerraBase.Features.Management.Queries;

/// <summary>
/// Bounded, memory-admitted query observations, flushed with usage every 10 seconds and at shutdown.
/// A crash loses observations not yet persisted.
/// </summary>
internal sealed partial class QueryShapeRecorder(SystemCatalog catalog, TimeProvider time, IMemoryBudget memory, IMeterFactory meters) : IQueryShapeTracker, IAsyncDisposable
{
    private const int MaxPending = 1024;
    private const int MaxRecords = 128;
    private const int MaxShapeBytes = 8 * 1024;
    // Saved UTF-16 shapes, records/dictionaries/response and two overlapping 1-MiB catalog pages while listing.
    private const long ReadBytes = MaxRecords * (2L * MaxShapeBytes + 2048) + 2L * 1024 * 1024;
    // Leaves room for database markers under the engine's 1024-write/7-MiB transaction bounds, even with JSON escaping.
    private const int MaxFlushShapes = 128;
    private const string Format = "alpha";
    private const string LastFlush = "flush";

    private readonly Counter<long> _dropped = meters.Create(Telemetry.MeterName).CreateCounter<long>(
        "terrabase.query_shapes.dropped", "{observation}", "Query shape observations dropped by validation, pending, memory or persisted limits.");

    // As in QueryFieldRecorder, never hold the in-memory lock across catalog I/O.
    private readonly Lock _gate = new();
    private readonly Dictionary<DatabaseOwner, Dictionary<(string Collection, string Shape), Entry>> _pending = [];
    private readonly SemaphoreSlim _flush = new(1, 1);
    private int _entries;
    private bool _disposed;
    private (string Id, Taken[] Batch)? _inDoubt;

    public void Record(TenantContext database, string collection, string shape, TimeSpan latency, long rowsScanned)
    {
        if (string.IsNullOrEmpty(database.OrganizationId) || database.OrganizationId.Length > 128
            || string.IsNullOrEmpty(database.ProjectId) || database.ProjectId.Length > 128
            || string.IsNullOrEmpty(database.DatabaseId) || database.DatabaseId.Length > 128 || !DatabaseName.TryParse(collection, out _)
            || shape.Length is 0 or > MaxShapeBytes || Encoding.UTF8.GetByteCount(shape) > MaxShapeBytes
            || latency < TimeSpan.Zero || rowsScanned < 0)
        {
            _dropped.Add(1, new KeyValuePair<string, object?>("reason", "invalid"));
            return;
        }

        var owner = Owner(database);
        var buckets = new long[QueryShapeStats.BucketCount];
        buckets[QueryShapeStats.Bucket(latency.TotalMilliseconds)] = 1;
        var observation = new Counts(1, latency.TotalMilliseconds, latency.TotalMilliseconds, rowsScanned, time.GetUtcNow(), buckets);
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            var pending = _pending.GetValueOrDefault(owner);
            var key = (collection, shape);
            if (pending?.GetValueOrDefault(key) is { } entry)
            {
                entry.Counts = Add(entry.Counts, observation);
                if (entry.Flushing)
                {
                    entry.AfterFlush = Add(entry.AfterFlush, observation);
                }

                return;
            }

            if (_entries >= MaxPending || (pending?.Count ?? 0) >= MaxRecords)
            {
                _dropped.Add(1, new KeyValuePair<string, object?>("reason", "pending_limit"));
                return;
            }

            // Retained UTF-16 strings, owner, dictionary entry, hash and snapshot/tail overhead; shape length is bounded.
            var bytes = 2048L + 2L * (shape.Length + collection.Length + owner.OrganizationId.Length + owner.ProjectId.Length + owner.DatabaseId.Length);
            var reservation = memory.TryReserve(MemoryLane.Foreground, bytes);
            if (reservation is null)
            {
                _dropped.Add(1, new KeyValuePair<string, object?>("reason", "memory"));
                return;
            }

            try
            {
                Span<byte> utf8 = stackalloc byte[MaxShapeBytes];
                var length = Encoding.UTF8.GetBytes(shape, utf8);
                Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
                SHA256.HashData(utf8[..length], hash);
                entry = new Entry(Convert.ToHexStringLower(hash), observation, reservation);
                if (pending is null)
                {
                    _pending[owner] = pending = [];
                }

                pending.Add(key, entry);
                _entries++;
            }
            catch
            {
                reservation.Dispose();
                throw;
            }
        }
    }

    public async ValueTask<QueryShapeReadResult> ReadAsync(TenantContext database, CancellationToken cancellationToken)
    {
        await _flush.WaitAsync(cancellationToken).ConfigureAwait(false);
        IMemoryReservation? reservation = null;
        try
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return new Unavailable("query shape statistics are shutting down");
                }
            }

            reservation = memory.TryReserve(MemoryLane.Foreground, ReadBytes);
            if (reservation is null)
            {
                return new Unavailable("query shape statistics are at memory capacity; retry later", "capacity");
            }
            if (await SettleAsync(cancellationToken).ConfigureAwait(false) is Unavailable refused)
            {
                return refused;
            }

            var owner = Owner(database);
            var admitted = reservation;
            var result = await catalog.ReadAsync<QueryShapeReadResult>(async transaction =>
            {
                var saved = await ReadSavedAsync(transaction, Records(owner.DatabaseId), cancellationToken).ConfigureAwait(false);
                if (saved is null)
                {
                    return new Unavailable("unsupported query shape statistics format or record count");
                }

                var stats = saved.ToDictionary(stat => Key(stat.Collection, stat.Id), StringComparer.Ordinal);
                foreach (var pending in Snapshot(owner))
                {
                    var key = Key(pending.Collection, pending.Id);
                    if (stats.TryGetValue(key, out var previous))
                    {
                        stats[key] = Merge(previous, pending);
                    }
                    else if (stats.Count < MaxRecords)
                    {
                        stats[key] = pending;
                    }
                }

                return new QueryShapeRead(new QueryShapeListBody([.. stats.Values.OrderByDescending(stat => stat.Queries)
                    .ThenBy(stat => stat.Collection, StringComparer.Ordinal).ThenBy(stat => stat.Id, StringComparer.Ordinal)]), admitted);
            }, static error => error, cancellationToken).ConfigureAwait(false);
            if (result is QueryShapeRead)
            {
                reservation = null;
            }

            return result;
        }
        finally
        {
            reservation?.Dispose();
            _flush.Release();
        }
    }

    public async ValueTask FlushAsync(CancellationToken cancellationToken)
    {
        await _flush.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }
            }

            if (await SettleAsync(cancellationToken).ConfigureAwait(false) is Unavailable)
            {
                return;
            }

            lock (_gate)
            {
                if (_pending.Count == 0)
                {
                    return;
                }
            }

            // Empty flushes never initialize the catalog; refusal retains every observation for retry.
            if (await catalog.CheckAsync(cancellationToken).ConfigureAwait(false) is Unavailable)
            {
                return;
            }

            Taken[] batch;
            lock (_gate)
            {
                batch = [.. _pending.Keys.Select(owner => new Taken(owner, Snapshot(owner)))];
                foreach (var pending in _pending.Values.SelectMany(entries => entries.Values))
                {
                    pending.Flushing = true;
                    pending.AfterFlush = default;
                }
            }

            var batches = batch.SelectMany(taken => taken.Shapes.Select(shape => (taken.Database, Shape: shape)))
                .Chunk(MaxFlushShapes)
                .Select(chunk => chunk.GroupBy(item => item.Database)
                    .Select(group => new Taken(group.Key, [.. group.Select(item => item.Shape)])).ToArray()).ToArray();
            for (var index = 0; index < batches.Length; index++)
            {
                var id = EntityIds.New().ToString();
                try
                {
                    var written = await WriteAsync(id, batches[index], cancellationToken).ConfigureAwait(false);
                    if (written is not ValueTuple<long, long> saved)
                    {
                        foreach (var remaining in batches.Skip(index))
                        {
                            Complete(remaining, committed: false);
                        }

                        return;
                    }

                    var (dropped, unsupported) = saved;
                    if (dropped > 0)
                    {
                        _dropped.Add(dropped, new KeyValuePair<string, object?>("reason", "persisted_limit"));
                    }
                    if (unsupported > 0)
                    {
                        _dropped.Add(unsupported, new KeyValuePair<string, object?>("reason", "unsupported_format"));
                    }
                }
                catch
                {
                    _inDoubt = (id, batches[index]);
                    // Later batches were never attempted: retain their aggregate and stop tracking a flush tail.
                    foreach (var remaining in batches.Skip(index + 1))
                    {
                        Complete(remaining, committed: false);
                    }

                    throw;
                }

                Complete(batches[index], committed: true);
            }
        }
        finally
        {
            _flush.Release();
        }
    }

    // The marker is saved with all records in one transaction. Disposal can throw after commit, so settle before retrying.
    private async ValueTask<CatalogCheck> SettleAsync(CancellationToken cancellationToken)
    {
        if (_inDoubt is not { } doubt)
        {
            return new Done();
        }

        var committed = await catalog.ReadAsync<SettleResult>(async transaction =>
        {
            foreach (var (database, _) in doubt.Batch)
            {
                if (await GetAsync(transaction, Flushes(database.DatabaseId), LastFlush, TerraBaseJsonContext.Default.String, cancellationToken).ConfigureAwait(false)
                    == doubt.Id)
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

        Complete(doubt.Batch, settled);
        _inDoubt = null;
        return new Done();
    }

    private ValueTask<FlushResult> WriteAsync(string id, Taken[] batch, CancellationToken cancellationToken) =>
        catalog.WriteAsync<FlushResult>(async transaction =>
            await WriteShapesAsync(transaction, batch, id, cancellationToken).ConfigureAwait(false), static error => error, cancellationToken);

    private static async ValueTask<(long Dropped, long Unsupported)> WriteShapesAsync(
        IStorageTransaction transaction, Taken[] batch, string? flushId, CancellationToken cancellationToken)
    {
        var dropped = 0L;
        var unsupported = 0L;
        foreach (var (database, shapes) in batch)
        {
            // Do not recreate database state deleted since the observation.
            if (await EngineManagementService.DatabaseTenantUncheckedAsync(
                transaction, database.OrganizationId, database.ProjectId, database.DatabaseId, cancellationToken).ConfigureAwait(false) is null)
            {
                continue;
            }

            var records = Records(database.DatabaseId);
            var count = await transaction.CountDocumentsAsync(records, cancellationToken).ConfigureAwait(false);
            foreach (var shape in shapes)
            {
                var key = Key(shape.Collection, shape.Id);
                var document = await transaction.GetAsync(records, key, cancellationToken).ConfigureAwait(false);
                var saved = document is null ? null : Read(document.Json, document.Id);
                // Leave incompatible records intact and their API unavailable, without blocking other observations.
                if (document is not null && saved is null)
                {
                    unsupported = Add(unsupported, shape.Queries);
                    continue;
                }

                if (saved is null)
                {
                    // ponytail: new shapes after the 128-record cap are dropped; add eviction when workloads need it.
                    if (count >= MaxRecords)
                    {
                        dropped = Add(dropped, shape.Queries);
                        continue;
                    }

                    count++;
                }

                var stats = saved is null ? shape : Merge(saved, shape);
                await PutAsync(transaction, records, key, new QueryShapeRecord(Format, stats), TerraBaseJsonContext.Default.QueryShapeRecord, cancellationToken).ConfigureAwait(false);
            }

            if (flushId is not null)
            {
                await PutAsync(transaction, Flushes(database.DatabaseId), LastFlush, flushId, TerraBaseJsonContext.Default.String, cancellationToken).ConfigureAwait(false);
            }
        }

        return (dropped, unsupported);
    }

    private void Complete(Taken[] batch, bool committed)
    {
        lock (_gate)
        {
            foreach (var (owner, shapes) in batch)
            {
                var pending = _pending[owner];
                foreach (var shape in shapes)
                {
                    var key = (shape.Collection, shape.Shape);
                    var entry = pending[key];
                    if (committed && entry.AfterFlush.Queries == 0)
                    {
                        pending.Remove(key);
                        entry.Reservation.Dispose();
                        _entries--;
                    }
                    else
                    {
                        if (committed)
                        {
                            entry.Counts = entry.AfterFlush;
                        }

                        entry.Flushing = false;
                        entry.AfterFlush = default;
                    }
                }

                if (pending.Count == 0)
                {
                    _pending.Remove(owner);
                }
            }
        }
    }

    // Same deterministic admission order for combined reads and flushes. The lock is re-entrant.
    private QueryShapeStats[] Snapshot(DatabaseOwner owner)
    {
        lock (_gate)
        {
            return _pending.TryGetValue(owner, out var pending)
                ? [.. pending.OrderBy(pair => pair.Key.Collection, StringComparer.Ordinal).ThenBy(pair => pair.Value.Id, StringComparer.Ordinal)
                    .Select(pair => Stats(pair.Value.Id, pair.Key.Collection, pair.Key.Shape, pair.Value.Counts))]
                : [];
        }
    }

    private static async ValueTask<List<QueryShapeStats>?> ReadSavedAsync(
        IStorageTransaction transaction, string collection, CancellationToken cancellationToken)
    {
        var stats = new List<QueryShapeStats>();
        string? after = null;
        DocumentPage page;
        do
        {
            page = await transaction.ListAsync(collection, after, MaxRecords, 1024 * 1024, cancellationToken).ConfigureAwait(false);
            foreach (var document in page.Documents)
            {
                if (stats.Count == MaxRecords || Read(document.Json, document.Id) is not { } saved)
                {
                    return null;
                }

                stats.Add(saved);
                after = document.Id;
            }
        }
        while (page.HasMore);

        return stats;
    }

    private static QueryShapeStats? Read(ReadOnlyMemory<byte> json, string id)
    {
        // Bound decoding, and inspect the envelope before decoding a payload this build may not understand.
        if (json.Length > 64 * 1024)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("format", out var format)
                || format.ValueKind != JsonValueKind.String || format.GetString() != Format
                || !root.TryGetProperty("stats", out var payload) || payload.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var stats = payload.Deserialize(TerraBaseJsonContext.Default.QueryShapeStats);
            return stats is not null && Valid(stats, id) ? stats : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static bool Valid(QueryShapeStats? stats, string id)
    {
        if (stats is null || string.IsNullOrEmpty(stats.Id) || !DatabaseName.TryParse(stats.Collection, out _)
            || string.IsNullOrEmpty(stats.Shape) || stats.Shape.Length > MaxShapeBytes
            || !stats.Shape.StartsWith("[\"alpha\",", StringComparison.Ordinal) || Encoding.UTF8.GetByteCount(stats.Shape) > MaxShapeBytes
            || stats.Queries <= 0 || stats.RowsScanned < 0 || stats.LastSeenAt == default
            || !double.IsFinite(stats.TotalLatencyMs) || !double.IsFinite(stats.MaxLatencyMs)
            || stats.MaxLatencyMs < 0 || stats.TotalLatencyMs < stats.MaxLatencyMs || Key(stats.Collection, stats.Id) != id
            || stats.LatencyBuckets is { } buckets && (buckets.Count != QueryShapeStats.BucketCount || buckets.Any(count => count < 0) || buckets.Sum() > stats.Queries))
        {
            return false;
        }

        Span<byte> utf8 = stackalloc byte[MaxShapeBytes];
        var length = Encoding.UTF8.GetBytes(stats.Shape, utf8);
        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(utf8[..length], hash);
        return stats.Id == Convert.ToHexStringLower(hash);
    }

    private static QueryShapeStats Merge(QueryShapeStats a, QueryShapeStats b) => Stats(a.Id, a.Collection, a.Shape, Add(
        new Counts(a.Queries, a.TotalLatencyMs, a.MaxLatencyMs, a.RowsScanned, a.LastSeenAt, a.LatencyBuckets),
        new Counts(b.Queries, b.TotalLatencyMs, b.MaxLatencyMs, b.RowsScanned, b.LastSeenAt, b.LatencyBuckets)));

    private static QueryShapeStats Stats(string id, string collection, string shape, Counts count) =>
        new(id, collection, shape, count.Queries, count.TotalLatencyMs, count.MaxLatencyMs, count.RowsScanned, count.LastSeenAt, count.Buckets);

    private static Counts Add(Counts a, Counts b) => new(
        Add(a.Queries, b.Queries), Math.Min(double.MaxValue, a.TotalLatencyMs + b.TotalLatencyMs), Math.Max(a.MaxLatencyMs, b.MaxLatencyMs),
        Add(a.RowsScanned, b.RowsScanned), a.LastSeenAt > b.LastSeenAt ? a.LastSeenAt : b.LastSeenAt,
        a.Buckets is null ? b.Buckets : b.Buckets is null ? a.Buckets : [.. a.Buckets.Zip(b.Buckets, Add)]);

    private static long Add(long a, long b) => b > long.MaxValue - a ? long.MaxValue : a + b;

    private static string Key(string collection, string id) => $"{collection}:{id}";
    private static DatabaseOwner Owner(TenantContext database) => DatabaseOwner.Of(database);
    private static string Records(string databaseId) => DatabaseState(databaseId) + "query_shapes";
    private static string Flushes(string databaseId) => DatabaseState(databaseId) + "query_shapes_flush";

    public async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            _disposed = true;
        }

        // The host can dispose DI while a shutdown flush is still finishing. Join it before releasing its entries.
        await _flush.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (_gate)
            {
                foreach (var entry in _pending.Values.SelectMany(entries => entries.Values))
                {
                    entry.Reservation.Dispose();
                }

                _pending.Clear();
                _entries = 0;
                _inDoubt = null;
            }
        }
        finally
        {
            // No wait handle is created; leave the semaphore usable for late shutdown calls and repeated DI disposal.
            _flush.Release();
        }
    }

    private readonly union SettleResult(bool, Unavailable);
    private readonly union FlushResult((long Dropped, long Unsupported), Unavailable);

    private readonly record struct Counts(
        long Queries, double TotalLatencyMs, double MaxLatencyMs, long RowsScanned, DateTimeOffset LastSeenAt, IReadOnlyList<long>? Buckets);
    private readonly record struct Taken(DatabaseOwner Database, QueryShapeStats[] Shapes);

    // Keep observations arriving during a flush separate so removing its batch preserves the next batch's maximum.
    private sealed class Entry(string id, Counts counts, IMemoryReservation reservation)
    {
        public string Id { get; } = id;
        public IMemoryReservation Reservation { get; } = reservation;
        public Counts Counts { get; set; } = counts;
        public bool Flushing { get; set; }
        public Counts AfterFlush { get; set; }
    }
}

/// <summary>Explicitly versioned catalog representation; public API names stay unversioned.</summary>
internal sealed record QueryShapeRecord(string Format, QueryShapeStats Stats);

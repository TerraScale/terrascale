using TerraBase.Hosting;
using TerraBase.Tenancy;
using static TerraBase.Features.Management.SystemCatalog;

namespace TerraBase.Features.Management.Queries;

/// <summary>
/// Counts the fields SQL runs used without an index in memory and saves them to the catalog in one write per flush, with
/// the usage counts (every 10 seconds and at shutdown). A run never waits for, or fails on, the catalog, and recommendations
/// read the pending counts without writing. The counts only drive index recommendations and are best-effort: a crash loses
/// those since the last flush, and fields first seen while the buffer is full are not counted.
/// </summary>
internal sealed class QueryFieldRecorder(SystemCatalog catalog, TimeProvider time) : IDisposable
{
    // Distinct fields held between flushes, in total; each database also holds at most QueryFieldUsage.MaxRecords.
    private const int MaxPending = 10_000;

    // The id of the last flush that added to a database's records, saved in the same write.
    private const string LastFlush = "flush";

    // Guards _pending and _entries; held only for in-memory work, never across an await.
    private readonly Lock _gate = new();
    private readonly Dictionary<DatabaseOwner, Dictionary<(string Collection, string Field), FieldCount>> _pending = [];
    private int _entries;

    // Serializes flushes and combined reads, including the gap between a commit and removing its pending counts.
    private readonly SemaphoreSlim _flush = new(1, 1);

    // A flush whose write threw may still have committed (the catalog commits before it disposes the transaction): its
    // counts stay pending until its id, saved with them, shows whether they were saved. Guarded by _flush.
    private (string Id, Taken[] Batch)? _inDoubt;

    public void Record(TenantContext database, string collection, IEnumerable<string> fields)
    {
        var owner = DatabaseOwner.Of(database);
        var now = time.GetUtcNow();
        lock (_gate)
        {
            foreach (var field in fields)
            {
                var pending = _pending.TryGetValue(owner, out var existing) ? existing : null;
                if (pending?.GetValueOrDefault((collection, field)) is { Queries: > 0 } count)
                {
                    pending[(collection, field)] = new FieldCount(count.Queries + 1, Later(count.LastSeenAt, now));
                }
                else if (_entries < MaxPending && (pending?.Count ?? 0) < QueryFieldUsage.MaxRecords)
                {
                    // A new field needs room in this database's share and in the total.
                    if (pending is null)
                    {
                        _pending[owner] = pending = [];
                    }

                    pending[(collection, field)] = new FieldCount(1, now);
                    _entries++;
                }
            }
        }
    }

    /// <summary>
    /// The saved usage records of a database with its pending counts added, without writing: a field not saved yet is
    /// added (in name order) while the database has room under <see cref="QueryFieldUsage.MaxRecords"/>, as a flush would.
    /// </summary>
    public async ValueTask<List<QueryFieldUsage>> ReadAsync(TenantContext database, CancellationToken cancellationToken)
    {
        await _flush.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await SettleAsync(cancellationToken).ConfigureAwait(false);
            var saved = await catalog.ReadAsync(
                transaction => ListAllAsync(transaction, QueryFieldUsage.Records(database.DatabaseId!), TerraBaseJsonContext.Default.QueryFieldUsage, cancellationToken),
                cancellationToken).ConfigureAwait(false);
            return WithPending(database, saved);
        }
        finally
        {
            _flush.Release();
        }
    }

    private List<QueryFieldUsage> WithPending(TenantContext database, IReadOnlyList<QueryFieldUsage> saved)
    {
        var owner = DatabaseOwner.Of(database);
        var usages = saved.ToDictionary(usage => QueryFieldUsage.Key(usage.Collection, usage.Field), StringComparer.Ordinal);
        foreach (var ((collection, field), count) in Snapshot(owner))
        {
            var key = QueryFieldUsage.Key(collection, field);
            if (usages.TryGetValue(key, out var usage))
            {
                usages[key] = usage with { Queries = usage.Queries + count.Queries, LastSeenAt = Later(usage.LastSeenAt, count.LastSeenAt) };
            }
            else if (usages.Count < QueryFieldUsage.MaxRecords)
            {
                usages[key] = new QueryFieldUsage(collection, field, count.Queries, count.LastSeenAt);
            }
        }

        return [.. usages.Values];
    }

    public async ValueTask FlushAsync(CancellationToken cancellationToken)
    {
        await _flush.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await SettleAsync(cancellationToken).ConfigureAwait(false);
            Taken[] batch;
            lock (_gate)
            {
                batch = [.. _pending.Keys.Select(owner => new Taken(owner, Snapshot(owner)))];
            }

            if (batch.Length == 0)
            {
                return;
            }

            var id = Guid.NewGuid().ToString("N");
            try
            {
                await WriteAsync(id, batch, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                _inDoubt = (id, batch);
                throw;
            }

            // Committed (or dropped for a deleted database): remove what this flush took, keeping what runs added meanwhile.
            Remove(batch);
        }
        finally
        {
            _flush.Release();
        }
    }

    // Decides a flush that threw: it committed if any database it saved holds its id (one transaction saves all or none),
    // and then its counts leave memory; otherwise they stay pending for the next flush. A failed read leaves it in doubt.
    private async ValueTask SettleAsync(CancellationToken cancellationToken)
    {
        if (_inDoubt is not { } doubt)
        {
            return;
        }

        var committed = await catalog.ReadAsync(async transaction =>
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
        }, cancellationToken).ConfigureAwait(false);
        if (committed)
        {
            Remove(doubt.Batch);
        }

        _inDoubt = null;
    }

    private ValueTask<int> WriteAsync(string id, Taken[] batch, CancellationToken cancellationToken) =>
        catalog.WriteAsync(async transaction =>
        {
            foreach (var (database, fields) in batch)
            {
                // A database deleted since the run has had its state removed: do not recreate it.
                if (await EngineManagementService.DatabaseTenantUncheckedAsync(
                    transaction, database.OrganizationId, database.ProjectId, database.DatabaseId, cancellationToken).ConfigureAwait(false) is null)
                {
                    continue;
                }

                var records = QueryFieldUsage.Records(database.DatabaseId);
                // ponytail: fields first used once the database has MaxRecords are dropped; evict the least used if dashboards hit the cap.
                var count = await transaction.CountDocumentsAsync(records, cancellationToken).ConfigureAwait(false);
                foreach (var ((collection, field), pending) in fields)
                {
                    var key = QueryFieldUsage.Key(collection, field);
                    var usage = await GetAsync(transaction, records, key, TerraBaseJsonContext.Default.QueryFieldUsage, cancellationToken).ConfigureAwait(false);
                    if (usage is null && count++ >= QueryFieldUsage.MaxRecords)
                    {
                        continue;
                    }

                    var counted = new QueryFieldUsage(
                        collection, field, (usage?.Queries ?? 0) + pending.Queries, Later(usage?.LastSeenAt ?? pending.LastSeenAt, pending.LastSeenAt));
                    await PutAsync(transaction, records, key, counted, TerraBaseJsonContext.Default.QueryFieldUsage, cancellationToken).ConfigureAwait(false);
                }

                await PutAsync(transaction, Flushes(database.DatabaseId), LastFlush, id, TerraBaseJsonContext.Default.String, cancellationToken).ConfigureAwait(false);
            }

            return 0;
        }, cancellationToken);

    private void Remove(Taken[] batch)
    {
        lock (_gate)
        {
            foreach (var (owner, fields) in batch)
            {
                var pending = _pending[owner];
                foreach (var (key, saved) in fields)
                {
                    var current = pending[key];
                    if (current.Queries == saved.Queries)
                    {
                        pending.Remove(key);
                        _entries--;
                    }
                    else
                    {
                        pending[key] = current with { Queries = current.Queries - saved.Queries };
                    }
                }

                if (pending.Count == 0)
                {
                    _pending.Remove(owner);
                }
            }
        }
    }

    // Beside the records, so a database delete removes it with them; not one of them, so it never counts against MaxRecords.
    private static string Flushes(string databaseId) => SystemCatalog.DatabaseState(databaseId) + "query_fields_flush";

    public void Dispose() => _flush.Dispose();

    // A database's pending counts in name order, so which new fields fill the last places under the cap does not
    // depend on hashing. Takes _gate, which is re-entrant.
    private KeyValuePair<(string Collection, string Field), FieldCount>[] Snapshot(DatabaseOwner owner)
    {
        lock (_gate)
        {
            return _pending.TryGetValue(owner, out var pending)
                ? [.. pending.OrderBy(entry => entry.Key.Collection, StringComparer.Ordinal).ThenBy(entry => entry.Key.Field, StringComparer.Ordinal)]
                : [];
        }
    }

    private static DateTimeOffset Later(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;

    private readonly record struct FieldCount(long Queries, DateTimeOffset LastSeenAt);

    // One database's pending counts, as a flush took them.
    private readonly record struct Taken(DatabaseOwner Database, KeyValuePair<(string Collection, string Field), FieldCount>[] Fields);
}

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

internal sealed record ObservationFilter(Func<UsageDimension, bool> Keep, Action<long> Admit, Func<long, IDisposable> Hold);

internal sealed record UsageTotals(long Reads, long Writes)
{
    public OpCounts Counts => new(Reads, Writes);
}

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

internal readonly union MonthUnitsResult(UsageTotals, Unavailable);

internal enum UsageScope
{
    Hour,

    Day,

    Lifetime,

    Minute,
}

internal readonly record struct UsageKey(DatabaseOwner Database, long Hour, UsageScope Scope = UsageScope.Hour, int Minute = 0)
{
    public bool In(UsageGranularity granularity) => granularity == UsageWindow.Minute ? Scope == UsageScope.Minute
    : Scope is UsageScope.Hour or UsageScope.Minute || (Scope == UsageScope.Day && granularity == UsageWindow.Day);

    public long Bucket(UsageGranularity granularity) => (Hour * 60 + Minute) / granularity.Minutes;
}

internal sealed record UsageCounts(OpCounts Lifetime, OpCounts[] Series);

internal readonly union UsageCountsResult(UsageCounts, Unavailable);

internal sealed partial class UsageMeter(SystemCatalog catalog, TimeProvider time, TerraBaseOptions options, ILogger<UsageMeter> logger, IMemoryBudget memory, IMeterFactory meters,
    IDatabaseStats stats)
    : IUsageMeter, IDisposable
{

    internal static bool ByteMeter(string meter) => meter is "client_bytes" or "storage_bytes";

    private const string Ops = "ops";

    private const string LastFlush = "flush";

    private const char NodeSeparator = '~';

    private const long MaxPageBytes = 1024 * 1024;

    internal const int FlushDatabases = 16;

    private const int PrunePage = 8;

    private readonly ConcurrentDictionary<string, string> _pruned = new(StringComparer.Ordinal);

    private readonly ConcurrentDictionary<DatabaseOwner, ConcurrentDictionary<UsageKey, OpCounts>> _pending = new();

    private readonly ReaderWriterLockSlim _maps = new();

    private readonly SemaphoreSlim _flush = new(1, 1);

    private readonly Lock _publication = new();
    private long _published;
    private (string Id, KeyValuePair<UsageKey, OpCounts>[] Batch)? _writing;

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

    internal int PendingDatabases
    {
        get
        {
            Publish();
            return _pending.Count;
        }
    }

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

    public async ValueTask<UsageCountsResult> GetAsync(
    TenantContext database, UsageWindow window, CancellationToken cancellationToken, Action? proceed = null)
    {
        return await ReadAsync(database, window, meters: false, filter: null, proceed, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<UsageCountsResult> GetObservationsAsync(
    TenantContext database, UsageWindow window, CancellationToken cancellationToken, Action? proceed = null, ObservationFilter? filter = null) =>
    await ReadAsync(database, window, meters: true, filter, proceed, cancellationToken).ConfigureAwait(false);

    private async ValueTask<UsageCountsResult> ReadAsync(
        TenantContext database, UsageWindow window, bool meters, ObservationFilter? filter, Action? proceed, CancellationToken cancellationToken)
    {

        OpCounts Shape(OpCounts counts)
        {
            return !meters ? Strip(counts, meters) : filter is null ? counts : Retain(counts, filter);
        }

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
        for (var attempt = 0; attempt < 3; attempt++)
        {
            Publish();
            proceed?.Invoke();
            long published;
            lock (_publication)
            {
                if (_inDoubt is not null)
                {
                    break;
                }

                published = _published;
            }

            var read = await ReadSavedAsync(owner, window, Parse, proceed, cancellationToken).ConfigureAwait(false);
            if (read is not SavedCounts { Saved: var saved, Flushed: var flushed })
            {
                return read switch
                {
                    Unavailable error => error,
                    SavedCounts => throw new UnreachableException(),
                };
            }

            lock (_publication)
            {
                if (_inDoubt is null && _published == published)
                {

                    var committed = _writing is { } writing && writing.Id == flushed ? writing.Batch : null;
                    return Merge(saved, committed);
                }
            }
        }

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

            var read = await ReadSavedAsync(owner, window, Parse, proceed, cancellationToken).ConfigureAwait(false);
            if (read is not SavedCounts { Saved: var saved })
            {
                return read switch
                {
                    Unavailable error => error,
                    SavedCounts => throw new UnreachableException(),
                };
            }

            lock (_publication)
            {
                return Merge(saved, null);
            }
        }
        finally
        {
            _flush.Release();
        }

        UsageCounts Merge(UsageCounts saved, KeyValuePair<UsageKey, OpCounts>[]? committed)
        {
            var (lifetime, series) = saved;
            foreach (var (key, entry) in _pending.TryGetValue(owner, out var pending) ? pending : [])
            {
                var counts = entry;
                foreach (var (savedKey, savedCounts) in committed ?? [])
                {
                    if (savedKey == key)
                    {
                        counts -= savedCounts;
                    }
                }

                counts = Shape(counts);
                lifetime += counts;
                if (key.In(window.Granularity) && window.IndexOf(key.Hour, key.Minute) is { } index)
                {
                    series[index] += counts;
                }
            }

            var oldestKept = OldestKept(window.Granularity, CurrentMinute());
            for (var i = 0; i < series.Length && window.First + i < oldestKept; i++)
            {
                series[i] = OpCounts.Zero;
            }

            return new UsageCounts(lifetime, series);
        }
    }

    private async ValueTask<SavedRead> ReadSavedAsync(DatabaseOwner owner, UsageWindow window, Func<StoredDocument, OpCounts> parse,
        Action? proceed, CancellationToken cancellationToken) =>
        await catalog.ReadAsync<SavedRead>(async transaction => new SavedCounts(
            new UsageCounts(
                await ReadLifetimeAsync(transaction, owner.DatabaseId, parse, cancellationToken).ConfigureAwait(false),
                await ReadSeriesAsync(transaction, owner.DatabaseId, window, parse, proceed, cancellationToken).ConfigureAwait(false)),
            await SystemCatalog.GetAsync(transaction, UsageCollection(owner.DatabaseId), Own(LastFlush), TerraBaseJsonContext.Default.String,
                cancellationToken).ConfigureAwait(false)),
            static error => error, cancellationToken).ConfigureAwait(false);

    private sealed record SavedCounts(UsageCounts Saved, string? Flushed);

    private readonly union SavedRead(SavedCounts, Unavailable);

    public async ValueTask FlushAsync(CancellationToken cancellationToken)
    {

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

        if (await catalog.CheckAsync(cancellationToken).ConfigureAwait(false) is Unavailable)
        {
            return;
        }

        var dropped = 0;
        foreach (var databases in batch.GroupBy(entry => entry.Key.Database).Chunk(FlushDatabases))
        {
            KeyValuePair<UsageKey, OpCounts>[] chunk = [.. databases.SelectMany(entries => entries)];
            var id = EntityIds.New().ToString();
            lock (_publication)
            {
                _writing = (id, chunk);
            }

            FlushResult written;
            try
            {
                written = await WriteAsync(id, chunk, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                lock (_publication)
                {
                    (_inDoubt, _writing) = ((id, chunk), null);
                }

                throw;
            }

            lock (_publication)
            {
                _writing = null;
                if (written is int)
                {
                    foreach (var (key, counts) in chunk)
                    {
                        Subtract(key, counts);
                    }

                    _published++;
                }
            }

            if (written is not int saved)
            {
                break;
            }

            dropped += saved;
        }

        DropEmptied();

        if (dropped > 0)
        {
            logger.ZLogDebug($"Dropped operation counts of {dropped:@Databases} deleted databases");
        }
    }

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

        lock (_publication)
        {
            if (settled)
            {
                foreach (var (key, counts) in doubt.Batch)
                {
                    Subtract(key, counts);
                }

                _published++;
            }

            _inDoubt = null;
        }

        return new Done();
    }

    private async ValueTask<FlushResult> WriteAsync(
        string id, KeyValuePair<UsageKey, OpCounts>[] batch, CancellationToken cancellationToken)
    {
        var minute = CurrentMinute();
        List<(string Collection, string OldestKept)> pruned = [];
        var result = await catalog.WriteAsync<FlushResult>(async transaction =>
        {
            pruned.Clear();

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

                if (await EngineManagementService.DatabaseTenantUncheckedAsync(
                    transaction, database.OrganizationId, database.ProjectId, database.DatabaseId, cancellationToken).ConfigureAwait(false) is null)
                {
                    dropped++;
                    foreach (var granularity in UsageWindow.Granularities)
                    {
                        _ = _pruned.TryRemove(Collection(database.DatabaseId, granularity), out _);
                    }

                    continue;
                }

                var usage = UsageCollection(database.DatabaseId);
                await AddAsync(transaction, usage, Own(Ops), entries.Aggregate(OpCounts.Zero, (sum, entry) => sum + entry.Value), cancellationToken).ConfigureAwait(false);
                await PutAsync(transaction, usage, Own(LastFlush), id, TerraBaseJsonContext.Default.String, cancellationToken).ConfigureAwait(false);
                foreach (var granularity in UsageWindow.Granularities)
                {
                    var buckets = Collection(database.DatabaseId, granularity);

                    var oldestKept = OldestKept(granularity, minute);
                    foreach (var bucket in entries.Where(entry => entry.Key.In(granularity)).GroupBy(entry => entry.Key.Bucket(granularity))
                        .Where(bucket => bucket.Key >= oldestKept))
                    {
                        var counts = bucket.Aggregate(OpCounts.Zero, (sum, entry) => sum + entry.Value);
                        await AddAsync(transaction, buckets, Own(granularity.Id(bucket.Key)), counts, cancellationToken).ConfigureAwait(false);
                    }

                    var oldest = granularity.Id(oldestKept);
                    if (!_pruned.TryGetValue(buckets, out var known) || known != oldest)
                    {
                        await PruneAsync(transaction, buckets, oldest, cancellationToken).ConfigureAwait(false);
                        pruned.Add((buckets, oldest));
                    }
                }
            }

            return dropped;
        }, static error => error, cancellationToken).ConfigureAwait(false);
        if (result is int)
        {
            foreach (var (collection, oldest) in pruned)
            {
                _pruned[collection] = oldest;
            }
        }

        return result;
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

    private static async ValueTask PruneAsync(IStorageTransaction transaction, string collection, string oldestKept, CancellationToken cancellationToken)
    {
        while (true)
        {
            var page = await transaction.ListAsync(collection, afterId: null, PrunePage, MaxPageBytes, cancellationToken).ConfigureAwait(false);
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

    private static long OldestKept(UsageGranularity granularity, long minute) => (minute / granularity.Minutes) - granularity.Kept + 1;

    private void Compact()
    {
        var minute = CurrentMinute();
        var hour = minute / 60;
        var held = (_inDoubt?.Batch ?? []).Select(entry => entry.Key).ToHashSet();

        using var publication = _publication.EnterScope();
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

    private static string ProjectMonths(string projectId) => ProjectState(projectId) + "usage-months";

    private static string OrganizationMonths(string organizationId) => $"organizations/{organizationId}/usage-months";

    private static string MonthId(long hour) => DateTimeOffset.UnixEpoch.AddHours(hour).ToString("yyyy-MM", CultureInfo.InvariantCulture);

    private string Own(string name) => name + NodeSeparator + options.MachineId;

    private static string Name(string id) => id.IndexOf(NodeSeparator, StringComparison.Ordinal) is >= 0 and var at ? id[..at] : id;

    private static string Collection(string databaseId, UsageGranularity granularity) => DatabaseState(databaseId) + granularity.Collection;

    private long CurrentMinute() => time.GetUtcNow().ToUnixTimeSeconds() / 60;

    private long CurrentHour() => time.GetUtcNow().ToUnixTimeSeconds() / 3600;

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

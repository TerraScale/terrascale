using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using TerraBase.Configuration;
using TerraBase.Engine;
using TerraBase.Errors;
using TerraBase.Features.Management;
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

    public long StorageByteHours { get; init; }

    public long SampledHour { get; init; } = -1;
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
    IDatabaseStats stats, PeerUsage peers)
    : IUsageMeter, IDisposable
{


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
        CountMonth(database, units, 0);
        RecordOperation(database, "read", 1);
        stats.Read(database);
    }

    public void RecordWrites(TenantContext database, int writes, long units)
    {
        Buffer(new(DatabaseOwner.Of(database), CurrentHour()), 0, writes, 0, units);
        CountMonth(database, 0, units);
        stats.Wrote(database, writes);
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

        OpCounts Parse(StoredRecord record)
        {
            if (!meters)
            {
                return JsonSerializer.Deserialize(record.Json.Span, ManagementJsonContext.Default.UsageTotals)!.Counts;
            }

            using var parsing = filter?.Hold(record.Json.Length * ParsedBytesPerJsonByte);
            return Shape(JsonSerializer.Deserialize(record.Json.Span, ManagementJsonContext.Default.OpCounts)!);
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

    private async ValueTask<SavedRead> ReadSavedAsync(DatabaseOwner owner, UsageWindow window, Func<StoredRecord, OpCounts> parse,
        Action? proceed, CancellationToken cancellationToken) =>
        await catalog.ReadAsync<SavedRead>(async transaction => new SavedCounts(
            new UsageCounts(
                await ReadLifetimeAsync(transaction, owner.DatabaseId, parse, cancellationToken).ConfigureAwait(false),
                await ReadSeriesAsync(transaction, owner.DatabaseId, window, parse, proceed, cancellationToken).ConfigureAwait(false)),
            await SystemCatalog.GetAsync(transaction, UsageKind(owner.DatabaseId), Own(LastFlush), TerraBaseJsonContext.Default.String,
                cancellationToken).ConfigureAwait(false)),
            static error => error, cancellationToken).ConfigureAwait(false);

    private sealed record SavedCounts(UsageCounts Saved, string? Flushed);

    private readonly union SavedRead(SavedCounts, Unavailable);

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
        IStorageTransaction transaction, string databaseId, Func<StoredRecord, OpCounts> parse, CancellationToken cancellationToken)
    {
        var lifetime = OpCounts.Zero;
        string? after = null;
        while (true)
        {
            var page = await transaction.ListAsync(UsageKind(databaseId), after, limit: 100, MaxPageBytes, cancellationToken).ConfigureAwait(false);
            foreach (var record in page.Records.Where(entry => Name(entry.Id) == Ops))
            {
                lifetime += parse(record);
            }

            if (!page.HasMore)
            {
                return lifetime;
            }

            after = page.Records[^1].Id;
        }
    }

    private static async ValueTask<OpCounts[]> ReadSeriesAsync(
        IStorageTransaction transaction, string databaseId, UsageWindow window, Func<StoredRecord, OpCounts> parse, Action? proceed,
        CancellationToken cancellationToken)
    {
        var series = Enumerable.Repeat(OpCounts.Zero, window.Count).ToArray();
        var kind = Kind(databaseId, window.Granularity);
        var after = window.Granularity.Id(window.First - 1);
        var end = window.Granularity.Id(window.First + window.Count);
        while (true)
        {
            proceed?.Invoke();
            var page = await transaction.ListAsync(kind, after, limit: 100, MaxPageBytes, cancellationToken).ConfigureAwait(false);
            foreach (var record in page.Records)
            {
                if (string.CompareOrdinal(record.Id, end) >= 0)
                {
                    return series;
                }

                var index = window.Granularity.Parse(Name(record.Id)) - window.First;

                if (index >= 0 && index < series.Length)
                {
                    series[index] += parse(record);
                }
            }

            if (!page.HasMore)
            {
                return series;
            }

            after = page.Records[^1].Id;
        }
    }

    public void Dispose()
    {
        _flush.Dispose();
        _maps.Dispose();
        DisposeMeasurements();
    }

    private static string UsageKind(string databaseId) => DatabaseState(databaseId) + "usage";


    private string Own(string name) => name + NodeSeparator + options.MachineId;

    private static string Name(string id) => id.IndexOf(NodeSeparator, StringComparison.Ordinal) is >= 0 and var at ? id[..at] : id;

    private static string Kind(string databaseId, UsageGranularity granularity) => DatabaseState(databaseId) + granularity.Kind;

    private long CurrentMinute() => time.GetUtcNow().ToUnixTimeSeconds() / 60;

    private long CurrentHour() => time.GetUtcNow().ToUnixTimeSeconds() / 3600;

}

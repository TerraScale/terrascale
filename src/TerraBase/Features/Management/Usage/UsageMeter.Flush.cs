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

internal sealed partial class UsageMeter
{

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
                if (await SystemCatalog.GetAsync(transaction, UsageKind(database), Own(LastFlush), TerraBaseJsonContext.Default.String, cancellationToken)
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
        List<(string Kind, string OldestKept)> pruned = [];
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

                if (await CatalogTables.RetiredAsync(transaction, database.OrganizationId, cancellationToken).ConfigureAwait(false)
                    || await CatalogTables.RetiredAsync(transaction, database.ProjectId, cancellationToken).ConfigureAwait(false)
                    || await CatalogTables.RetiredAsync(transaction, database.DatabaseId, cancellationToken).ConfigureAwait(false))
                {
                    dropped++;
                    foreach (var granularity in UsageWindow.Granularities)
                    {
                        _ = _pruned.TryRemove(Kind(database.DatabaseId, granularity), out _);
                    }

                    continue;
                }

                var usage = UsageKind(database.DatabaseId);
                await AddAsync(transaction, usage, Own(Ops), entries.Aggregate(OpCounts.Zero, (sum, entry) => sum + entry.Value), cancellationToken).ConfigureAwait(false);
                await PutAsync(transaction, usage, Own(LastFlush), id, TerraBaseJsonContext.Default.String, cancellationToken).ConfigureAwait(false);
                foreach (var granularity in UsageWindow.Granularities)
                {
                    var buckets = Kind(database.DatabaseId, granularity);

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
            foreach (var (kind, oldest) in pruned)
            {
                _pruned[kind] = oldest;
            }
        }

        return result;
    }

    private static async ValueTask AddAsync(IStorageTransaction transaction, string kind, string id, OpCounts counts, CancellationToken cancellationToken)
    {
        var saved = await SystemCatalog.GetAsync(transaction, kind, id, ManagementJsonContext.Default.OpCounts, cancellationToken).ConfigureAwait(false);
        await PutAsync(transaction, kind, id, (saved ?? OpCounts.Zero) + counts, ManagementJsonContext.Default.OpCounts, cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask AddUnitsAsync(IStorageTransaction transaction, string kind, string id, UsageTotals units, CancellationToken cancellationToken)
    {
        var saved = await SystemCatalog.GetAsync(transaction, kind, id, ManagementJsonContext.Default.UsageTotals, cancellationToken).ConfigureAwait(false);
        await PutAsync(transaction, kind, id, (saved ?? new UsageTotals(0, 0)) with { Reads = (saved?.Reads ?? 0) + units.Reads, Writes = (saved?.Writes ?? 0) + units.Writes },
            ManagementJsonContext.Default.UsageTotals, cancellationToken).ConfigureAwait(false);
    }


    private static async ValueTask PruneAsync(IStorageTransaction transaction, string kind, string oldestKept, CancellationToken cancellationToken)
    {
        while (true)
        {
            var page = await transaction.ListAsync(kind, afterId: null, PrunePage, MaxPageBytes, cancellationToken).ConfigureAwait(false);
            var expired = page.Records.TakeWhile(record => string.CompareOrdinal(record.Id, oldestKept) < 0).ToList();
            foreach (var record in expired)
            {
                await transaction.DeleteAsync(kind, record.Id, cancellationToken).ConfigureAwait(false);
            }

            if (expired.Count < page.Records.Count || !page.HasMore)
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

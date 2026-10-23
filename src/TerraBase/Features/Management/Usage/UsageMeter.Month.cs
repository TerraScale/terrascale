using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using TerraBase.Tenancy;
using ZLogger;

namespace TerraBase.Features.Management.Usage;

internal sealed partial class UsageMeter
{
    // Per organization, each database read this UTC month: its saved and pending operations at that read plus those counted since.
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<DatabaseOwner, StrongBox<long>>> _month = new(StringComparer.Ordinal);
    private long _monthOf = -1;

    /// <summary>
    /// Reads and writes this UTC month of the organization's databases this process has served this month, including
    /// <paramref name="database"/>. Each database's saved usage is read once per month in the background, so a write never
    /// waits for a flush; until that read lands only operations counted since are included.
    /// </summary>
    internal long MonthOperations(TenantContext database)
    {
        var now = time.GetUtcNow();
        if (Interlocked.Exchange(ref _monthOf, (now.Year * 12) + now.Month) != (now.Year * 12) + now.Month)
        {
            _month.Clear();
        }

        var owner = DatabaseOwner.Of(database);
        var databases = _month.GetOrAdd(owner.OrganizationId, _ => new());
        if (databases.TryAdd(owner, new(0)))
        {
            _ = SeedMonthAsync(database, databases, owner, now);
        }

        return databases.Values.Sum(count => Interlocked.Read(ref count.Value));
    }

    // Adds the month's saved and pending operations at the read, less those the entry already counted by then.
    private async Task SeedMonthAsync(TenantContext database, ConcurrentDictionary<DatabaseOwner, StrongBox<long>> databases, DatabaseOwner owner, DateTimeOffset now)
    {
        var first = (new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero) - DateTimeOffset.UnixEpoch).Ticks / TimeSpan.TicksPerDay;
        var today = (now - DateTimeOffset.UnixEpoch).Ticks / TimeSpan.TicksPerDay;
        try
        {
            var read = await GetAsync(database, new UsageWindow(UsageWindow.Day, first, (int)(today - first + 1)), CancellationToken.None).ConfigureAwait(false);
            if (read is UsageCounts counts && databases.TryGetValue(owner, out var count))
            {
                // shortcut: operations counted between the read and this line are missed, a few at most, once per database and month.
                Interlocked.Add(ref count.Value, counts.Series.Sum(bucket => bucket.Reads + bucket.Writes) - Interlocked.Read(ref count.Value));
                return;
            }
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            logger.ZLogWarning(error, $"Reading the month's usage of database {owner.DatabaseId:@Database} failed; the next check reads it again");
        }

        // The next check reads it again; that read includes everything this entry counted.
        databases.TryRemove(owner, out _);
    }

    private void CountMonth(TenantContext database, long operations)
    {
        if (_month.TryGetValue(database.OrganizationId, out var databases) && databases.TryGetValue(DatabaseOwner.Of(database), out var count))
        {
            Interlocked.Add(ref count.Value, operations);
        }
    }
}

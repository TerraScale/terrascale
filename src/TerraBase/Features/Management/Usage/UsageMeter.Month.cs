using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using TerraBase.Tenancy;

namespace TerraBase.Features.Management.Usage;

internal sealed partial class UsageMeter
{
    // Per organization, each database read this UTC month: its saved and pending operations at that read plus those counted since.
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<DatabaseOwner, StrongBox<long>>> _month = new(StringComparer.Ordinal);
    private long _monthOf = -1;

    // Databases whose saved month the next seeding pass reads, with the tenant to read it as.
    private readonly ConcurrentDictionary<DatabaseOwner, TenantContext> _unseeded = new();

    /// <summary>
    /// Reads and writes this UTC month of the organization's databases this process has served this month, including
    /// <paramref name="database"/>. A request never reads storage for it: each database's saved month is added by the
    /// next <see cref="SeedMonthsAsync"/> pass, and until then only operations counted since are included.
    /// </summary>
    internal long MonthOperations(TenantContext database)
    {
        var now = time.GetUtcNow();
        if (Interlocked.Exchange(ref _monthOf, (now.Year * 12) + now.Month) != (now.Year * 12) + now.Month)
        {
            _month.Clear();
            _unseeded.Clear();
        }

        var owner = DatabaseOwner.Of(database);
        var databases = _month.GetOrAdd(owner.OrganizationId, _ => new());
        if (databases.TryAdd(owner, new(0)))
        {
            _unseeded.TryAdd(owner, database);
        }

        return databases.Values.Sum(count => Interlocked.Read(ref count.Value));
    }

    /// <summary>Adds each newly served database's saved and pending operations this month; a failed read is retried next pass.</summary>
    internal async ValueTask SeedMonthsAsync(CancellationToken cancellationToken)
    {
        foreach (var (owner, database) in _unseeded)
        {
            var now = time.GetUtcNow();
            var first = (new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero) - DateTimeOffset.UnixEpoch).Ticks / TimeSpan.TicksPerDay;
            var today = (now - DateTimeOffset.UnixEpoch).Ticks / TimeSpan.TicksPerDay;
            var read = await GetAsync(database, new UsageWindow(UsageWindow.Day, first, (int)(today - first + 1)), cancellationToken).ConfigureAwait(false);
            if (read is not UsageCounts counts)
            {
                continue;
            }

            if (_unseeded.TryRemove(owner, out _) && _month.TryGetValue(owner.OrganizationId, out var databases) && databases.TryGetValue(owner, out var count))
            {
                // The read already holds what the entry counted before it: replace those with the read's total.
                // shortcut: operations counted between the read and this line are missed, a few at most, once per database and month.
                Interlocked.Add(ref count.Value, counts.Series.Sum(bucket => bucket.Reads + bucket.Writes) - Interlocked.Read(ref count.Value));
            }
        }
    }

    private void CountMonth(TenantContext database, long operations)
    {
        if (_month.TryGetValue(database.OrganizationId, out var databases) && databases.TryGetValue(DatabaseOwner.Of(database), out var count))
        {
            Interlocked.Add(ref count.Value, operations);
        }
    }
}

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using TerraBase.Errors;
using TerraBase.Tenancy;

namespace TerraBase.Features.Management.Usage;

/// <summary>An organization's document operations this UTC month, or why they cannot be read now.</summary>
internal readonly union MonthOperationsResult(long, Unavailable);

internal sealed partial class UsageMeter
{
    // Per organization, each database read this UTC month: its saved and pending operations at that read plus those counted since.
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<DatabaseOwner, StrongBox<long>>> _month = new(StringComparer.Ordinal);
    private long _monthOf = -1;

    /// <summary>
    /// Reads and writes this UTC month of the organization's databases this process has served this month, including
    /// <paramref name="database"/>; each database's saved usage is read once per month, then counted in memory.
    /// </summary>
    internal async ValueTask<MonthOperationsResult> MonthOperationsAsync(TenantContext database, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        if (Interlocked.Exchange(ref _monthOf, (now.Year * 12) + now.Month) != (now.Year * 12) + now.Month)
        {
            _month.Clear();
        }

        var owner = DatabaseOwner.Of(database);
        var databases = _month.GetOrAdd(owner.OrganizationId, _ => new());
        if (!databases.ContainsKey(owner))
        {
            var first = (new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero) - DateTimeOffset.UnixEpoch).Ticks / TimeSpan.TicksPerDay;
            var today = (now - DateTimeOffset.UnixEpoch).Ticks / TimeSpan.TicksPerDay;
            var read = await GetAsync(database, new UsageWindow(UsageWindow.Day, first, (int)(today - first + 1)), cancellationToken).ConfigureAwait(false);
            if (read is not UsageCounts counts)
            {
                return read switch
                {
                    Unavailable error => error,
                    UsageCounts => throw new UnreachableException(),
                };
            }

            // shortcut: operations counted between this read and the entry are missed, a few at most, once per database and month.
            databases.TryAdd(owner, new(counts.Series.Sum(bucket => bucket.Reads + bucket.Writes)));
        }

        return databases.Values.Sum(count => Interlocked.Read(ref count.Value));
    }

    private void CountMonth(TenantContext database, long operations)
    {
        if (_month.TryGetValue(database.OrganizationId, out var databases) && databases.TryGetValue(DatabaseOwner.Of(database), out var count))
        {
            Interlocked.Add(ref count.Value, operations);
        }
    }
}

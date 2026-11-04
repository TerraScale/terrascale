using System.Collections.Concurrent;
using System.Diagnostics;
using TerraBase.Errors;
using TerraBase.Tenancy;

namespace TerraBase.Features.Management.Usage;

internal sealed record MonthUnits(long Reads, long Writes);

internal readonly union MonthUnitsCheck(MonthUnits, Unavailable);

internal sealed partial class UsageMeter
{
    private readonly ConcurrentDictionary<string, MonthCounter> _month = new(StringComparer.Ordinal);
    private long _monthOf = -1;

    internal async ValueTask<MonthUnitsCheck> MonthUnitsAsync(TenantContext database, CancellationToken cancellationToken)
    {
        RollOver();
        return _month.TryGetValue(database.OrganizationId, out var units)
            ? units.Read()
            : await RefreshAsync(database.OrganizationId, cancellationToken).ConfigureAwait(false);
    }

    internal async ValueTask RefreshMonthsAsync(CancellationToken cancellationToken)
    {
        RollOver();
        foreach (var organizationId in _month.Keys)
        {
            _ = await RefreshAsync(organizationId, cancellationToken).ConfigureAwait(false);
        }
    }

    private async ValueTask<MonthUnitsCheck> RefreshAsync(string organizationId, CancellationToken cancellationToken)
    {
        var read = await GetMonthAsync(organizationId, projectId: null, cancellationToken).ConfigureAwait(false);
        if (read is not UsageTotals units)
        {
            return read switch
            {
                Unavailable error => error,
                UsageTotals => throw new UnreachableException(),
            };
        }

        var counter = _month.GetOrAdd(organizationId, _ => new MonthCounter());
        counter.Set(units.Reads, units.Writes);
        return counter.Read();
    }

    private void RollOver()
    {
        var now = time.GetUtcNow();
        if (Interlocked.Exchange(ref _monthOf, (now.Year * 12) + now.Month) != (now.Year * 12) + now.Month)
        {
            _month.Clear();
        }
    }

    private void CountMonth(TenantContext database, long reads, long writes)
    {
        if (_month.TryGetValue(database.OrganizationId, out var count))
        {
            count.Add(reads, writes);
        }
    }

    private sealed class MonthCounter
    {
        private long _reads;
        private long _writes;

        public MonthUnits Read() => new(Interlocked.Read(ref _reads), Interlocked.Read(ref _writes));

        public void Set(long reads, long writes)
        {
            Interlocked.Exchange(ref _reads, reads);
            Interlocked.Exchange(ref _writes, writes);
        }

        public void Add(long reads, long writes)
        {
            Interlocked.Add(ref _reads, reads);
            Interlocked.Add(ref _writes, writes);
        }
    }
}

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using TerraBase.Errors;
using TerraBase.Tenancy;

namespace TerraBase.Features.Management.Usage;

/// <summary>An organization's operation units this month, or why they cannot be read now.</summary>
internal readonly union MonthUnitsCheck(long, Unavailable);

internal sealed partial class UsageMeter
{
    // Per organization, its operation units this UTC month in every region as of the last refresh, plus those this node counted since.
    private readonly ConcurrentDictionary<string, StrongBox<long>> _month = new(StringComparer.Ordinal);
    private long _monthOf = -1;

    /// <summary>
    /// Read and write operation units this UTC month of the organization of <paramref name="database"/>, in every region:
    /// <see cref="GetMonthAsync"/> as of the last <see cref="RefreshMonthsAsync"/> pass, plus the units this node counted
    /// since. The organization's first check this month on this node reads it before answering.
    /// </summary>
    internal async ValueTask<MonthUnitsCheck> MonthUnitsAsync(TenantContext database, CancellationToken cancellationToken)
    {
        RollOver();
        return _month.TryGetValue(database.OrganizationId, out var units)
            ? Interlocked.Read(ref units.Value)
            : await RefreshAsync(database.OrganizationId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads every checked organization's month again, adding what other regions flushed; a failed read keeps the last.</summary>
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

        // shortcut: units this node counts while the read runs may be missed until the next refresh (one 10 s tick).
        var total = units.Reads + units.Writes;
        _month.AddOrUpdate(organizationId, _ => new(total), (_, box) =>
        {
            Interlocked.Exchange(ref box.Value, total);
            return box;
        });
        return total;
    }

    private void RollOver()
    {
        var now = time.GetUtcNow();
        if (Interlocked.Exchange(ref _monthOf, (now.Year * 12) + now.Month) != (now.Year * 12) + now.Month)
        {
            _month.Clear();
        }
    }

    private void CountMonth(TenantContext database, long units)
    {
        if (_month.TryGetValue(database.OrganizationId, out var count))
        {
            Interlocked.Add(ref count.Value, units);
        }
    }
}

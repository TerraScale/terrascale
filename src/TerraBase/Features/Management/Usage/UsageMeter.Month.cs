using System.Collections.Concurrent;
using System.Diagnostics;
using TerraBase.Errors;
using TerraBase.Tenancy;
using ZLogger;

namespace TerraBase.Features.Management.Usage;

internal sealed record MonthUnits(long Reads, long Writes);

internal readonly union MonthUnitsCheck(MonthUnits, Unavailable);

internal sealed partial class UsageMeter
{
    private readonly ConcurrentDictionary<string, MonthCounter> _month = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, MonthUnits> _others = new(StringComparer.Ordinal);
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
        var organizations = _month.Keys.ToList();
        await RefreshOthersAsync(organizations, cancellationToken).ConfigureAwait(false);
        foreach (var organizationId in organizations)
        {
            _ = await RefreshAsync(organizationId, cancellationToken, others: false).ConfigureAwait(false);
        }
    }

    private async ValueTask<MonthUnitsCheck> RefreshAsync(string organizationId, CancellationToken cancellationToken, bool others = true)
    {
        if (others)
        {
            await RefreshOthersAsync([organizationId], cancellationToken).ConfigureAwait(false);
        }

        var read = await GetMonthAsync(organizationId, projectId: null, cancellationToken).ConfigureAwait(false);
        if (read is not UsageTotals units)
        {
            return read switch
            {
                Unavailable error => error,
                UsageTotals => throw new UnreachableException(),
            };
        }

        var remote = _others.GetValueOrDefault(organizationId) ?? new MonthUnits(0, 0);
        var counter = _month.GetOrAdd(organizationId, _ => new MonthCounter());
        counter.Set(units.Reads + remote.Reads, units.Writes + remote.Writes);
        return counter.Read();
    }

    private async ValueTask RefreshOthersAsync(List<string> organizations, CancellationToken cancellationToken)
    {
        if (organizations.Count == 0)
        {
            return;
        }

        var month = MonthId(CurrentHour());
        if (await peers.MonthsAsync(organizations, month, cancellationToken).ConfigureAwait(false) is not { } answered)
        {
            logger.ZLogWarning($"Another region did not answer its monthly units; plan limits count its last answer");
            return;
        }

        foreach (var (organizationId, units) in answered)
        {
            _others[organizationId] = new MonthUnits(units.Reads, units.Writes);
        }
    }

    private void RollOver()
    {
        var now = time.GetUtcNow();
        if (Interlocked.Exchange(ref _monthOf, (now.Year * 12) + now.Month) != (now.Year * 12) + now.Month)
        {
            _month.Clear();
            _others.Clear();
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

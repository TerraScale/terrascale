using System.Globalization;
using TerraBase.Engine;
using TerraBase.Errors;
using TerraBase.Tenancy;

namespace TerraBase.Features.Qos;

public readonly union StorageQuotaEntry(StorageQuotaLease, Unavailable);

public readonly union StorageQuotaGroupEntry(StorageQuotaGroupLease, Unavailable);

public readonly union StorageQuotaBytesResult(StorageUsage, Unavailable);

public sealed record StorageUsage(long Known, long Unverified, bool Unbounded);

public readonly union StorageQuotaCheck(Done, QuotaExceeded, Unavailable);

public interface IStorageQuota
{
    ValueTask<StorageQuotaEntry> EnterAsync(TenantContext database, IDatabaseStore store, CancellationToken cancellationToken);

    ValueTask<StorageQuotaGroupEntry> EnterOrganizationsAsync(IReadOnlyList<string> organizationIds, CancellationToken cancellationToken);

    ValueTask<long> ReadBytesAsync(TenantContext database, CancellationToken cancellationToken);

    ValueTask<OrganizationBytesResult> ReadOrganizationsAsync(CancellationToken cancellationToken);
}

public readonly union OrganizationBytesResult(IReadOnlyDictionary<string, long>, Unavailable);

public sealed class StorageQuotaLease : IDisposable
{
    private readonly SemaphoreSlim? _gate;
    private readonly long _limit;
    private readonly Func<CancellationToken, ValueTask<StorageQuotaBytesResult>> _measure;
    private readonly Action<long> _committed;
    private int _disposed;

    internal StorageQuotaLease(SemaphoreSlim? gate, long limit, Func<CancellationToken, ValueTask<StorageQuotaBytesResult>> measure, Action<long> committed)
    {
        _gate = gate;
        _limit = limit;
        _measure = measure;
        _committed = committed;
    }

    public bool RequiresAccounting => _limit != long.MaxValue;

    public async ValueTask<StorageQuotaCheck> CheckAsync(long delta, CancellationToken cancellationToken)
    {
        if (delta <= 0 || _limit == long.MaxValue)
        {
            return new Done();
        }

        try
        {
            return StorageQuotaRule.Growth(_limit, delta, await _measure(cancellationToken).ConfigureAwait(false));
        }
        catch (QueryCapacityException)
        {
            return StorageQuota.AccountingUnavailable;
        }
    }

    public void Committed(long delta) => _committed(delta);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _gate?.Release();
        }
    }
}

public sealed class StorageQuotaGroupLease : IDisposable
{
    private readonly SemaphoreSlim[] _gates;
    private readonly IReadOnlyDictionary<string, long> _limits;
    private readonly Func<string, CancellationToken, ValueTask<StorageQuotaBytesResult>> _measure;
    private int _disposed;

    internal StorageQuotaGroupLease(SemaphoreSlim[] gates, IReadOnlyDictionary<string, long> limits,
        Func<string, CancellationToken, ValueTask<StorageQuotaBytesResult>> measure)
    {
        _gates = gates;
        _limits = limits;
        _measure = measure;
    }

    public bool RequiresAccounting(string organizationId) => _limits[organizationId] != long.MaxValue;

    public async ValueTask<StorageQuotaCheck> CheckAsync(string organizationId, long delta, CancellationToken cancellationToken)
    {
        var limit = _limits[organizationId];
        if (delta <= 0 || limit == long.MaxValue)
        {
            return new Done();
        }

        try
        {
            return StorageQuotaRule.Growth(limit, delta, await _measure(organizationId, cancellationToken).ConfigureAwait(false));
        }
        catch (QueryCapacityException)
        {
            return StorageQuota.AccountingUnavailable;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            for (var i = _gates.Length - 1; i >= 0; i--)
            {
                _gates[i].Release();
            }
        }
    }
}

static file class StorageQuotaRule
{
    internal static StorageQuotaCheck Growth(long limit, long delta, StorageQuotaBytesResult counted) => counted switch
    {
        StorageUsage { Known: var used } when used > limit || delta > limit - used =>
            new QuotaExceeded(string.Create(CultureInfo.InvariantCulture,
                $"the organization storage quota would be exceeded: {used / 1_000_000_000m:F2} GB used of {limit / 1_000_000_000m:F2} GB on this plan; delete data or upgrade the plan at https://dash.terrascale.tech/organization?tab=billing"),
                "storage_quota"),
        StorageUsage usage when usage.Unbounded || delta > limit - usage.Known - usage.Unverified => StorageQuota.AccountingPending,
        StorageUsage => new Done(),
        Unavailable error => error,
    };
}

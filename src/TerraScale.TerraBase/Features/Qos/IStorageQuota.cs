using TerraBase.Engine;
using TerraBase.Errors;
using TerraBase.Tenancy;

namespace TerraBase.Features.Qos;

/// <summary>Serializes node-local document writes and measures live JSON bytes across an organization's databases.</summary>
public interface IStorageQuota
{
    /// <summary>Holds organization admission until the caller commits or rolls back; cancellation releases admission.</summary>
    ValueTask<StorageQuotaLease> EnterAsync(TenantContext database, IDatabaseStore store, CancellationToken cancellationToken);

    /// <summary>Holds up to sixteen organizations through lifecycle publication, acquiring distinct stripes in order.</summary>
    ValueTask<StorageQuotaGroupLease> EnterOrganizationsAsync(IReadOnlyList<string> organizationIds, CancellationToken cancellationToken);

    /// <summary>Measures an exact staged copy before its catalog registration.</summary>
    ValueTask<long> ReadBytesAsync(TenantContext database, CancellationToken cancellationToken);
}

/// <summary>A measured organization write, held through its database commit. It does not reserve durable quota.</summary>
public sealed class StorageQuotaLease : IDisposable
{
    private readonly SemaphoreSlim? _gate;
    private readonly long _limit;
    private readonly Func<CancellationToken, ValueTask<long>> _measure;
    private readonly Action<long> _committed;
    private int _disposed;

    internal StorageQuotaLease(SemaphoreSlim? gate, long limit, Func<CancellationToken, ValueTask<long>> measure, Action<long> committed)
    {
        _gate = gate;
        _limit = limit;
        _measure = measure;
        _committed = committed;
    }

    /// <summary>Refuses net growth above the limit; reducing and unchanged writes remain available after a downgrade.</summary>
    public async ValueTask<Failure?> CheckAsync(long delta, CancellationToken cancellationToken)
    {
        if (delta <= 0 || _limit == long.MaxValue)
        {
            return null;
        }

        long used;
        try
        {
            used = await _measure(cancellationToken).ConfigureAwait(false);
        }
        catch (QueryCapacityException)
        {
            return StorageQuota.AccountingUnavailable;
        }
        if (used > _limit || delta > _limit - used)
        {
            return new QuotaExceeded("the organization storage quota would be exceeded", "storage_quota");
        }

        return null;
    }

    /// <summary>Updates the bounded usage cache only after the database commit succeeds.</summary>
    public void Committed(long delta) => _committed(delta);

    /// <summary>Releases write admission; safe to call more than once.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _gate?.Release();
        }
    }
}

/// <summary>Organization lifecycle gates held through catalog commit or rollback.</summary>
public sealed class StorageQuotaGroupLease : IDisposable
{
    private readonly SemaphoreSlim[] _gates;
    private readonly IReadOnlyDictionary<string, long> _limits;
    private readonly Func<string, CancellationToken, ValueTask<long>> _measure;
    private int _disposed;

    internal StorageQuotaGroupLease(SemaphoreSlim[] gates, IReadOnlyDictionary<string, long> limits,
        Func<string, CancellationToken, ValueTask<long>> measure)
    {
        _gates = gates;
        _limits = limits;
        _measure = measure;
    }

    /// <summary>Whether the gated live plan has a byte ceiling; enterprise copies need no accounting scan.</summary>
    public bool RequiresAccounting(string organizationId) => _limits[organizationId] != long.MaxValue;

    /// <summary>Checks net staged growth before taking the system catalog writer; reducing changes remain available.</summary>
    public async ValueTask<Failure?> CheckAsync(string organizationId, long delta, CancellationToken cancellationToken)
    {
        var limit = _limits[organizationId];
        if (delta <= 0 || limit == long.MaxValue)
        {
            return null;
        }

        long used;
        try
        {
            used = await _measure(organizationId, cancellationToken).ConfigureAwait(false);
        }
        catch (QueryCapacityException)
        {
            return StorageQuota.AccountingUnavailable;
        }
        if (used > limit || delta > limit - used)
        {
            return new QuotaExceeded("the organization storage quota would be exceeded", "storage_quota");
        }

        return null;
    }

    /// <summary>Releases all stripes in reverse acquisition order; safe to call more than once.</summary>
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

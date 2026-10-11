using TerraBase.Engine;
using TerraBase.Errors;
using TerraBase.Tenancy;

namespace TerraBase.Features.Qos;

/// <summary>A write admission or a catalog refusal.</summary>
public readonly union StorageQuotaEntry(StorageQuotaLease, Unavailable);

/// <summary>Lifecycle admissions or a catalog refusal.</summary>
public readonly union StorageQuotaGroupEntry(StorageQuotaGroupLease, Unavailable);

/// <summary>Measured bytes or unavailable catalog accounting.</summary>
public readonly union StorageQuotaBytesResult(long, Unavailable);

/// <summary>Accepted growth, a storage quota refusal or unavailable accounting.</summary>
public readonly union StorageQuotaCheck(Done, QuotaExceeded, Unavailable);

/// <summary>Serializes node-local document writes and measures live JSON bytes across an organization's databases.</summary>
public interface IStorageQuota
{
    /// <summary>Holds organization admission until the caller commits or rolls back; cancellation releases admission.</summary>
    ValueTask<StorageQuotaEntry> EnterAsync(TenantContext database, IDatabaseStore store, CancellationToken cancellationToken);

    /// <summary>Holds up to sixteen organizations through lifecycle publication, acquiring distinct stripes in order.</summary>
    ValueTask<StorageQuotaGroupEntry> EnterOrganizationsAsync(IReadOnlyList<string> organizationIds, CancellationToken cancellationToken);

    /// <summary>Measures an exact staged copy before its catalog registration.</summary>
    ValueTask<long> ReadBytesAsync(TenantContext database, CancellationToken cancellationToken);
}

/// <summary>A measured organization write, held through its database commit. It does not reserve durable quota.</summary>
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

    /// <summary>Whether this write needs commercial byte accounting.</summary>
    public bool RequiresAccounting => _limit != long.MaxValue;

    /// <summary>Refuses net growth above the limit; reducing and unchanged writes remain available after a downgrade.</summary>
    public async ValueTask<StorageQuotaCheck> CheckAsync(long delta, CancellationToken cancellationToken)
    {
        if (delta <= 0 || _limit == long.MaxValue)
        {
            return new Done();
        }

        long used;
        try
        {
            var counted = await _measure(cancellationToken).ConfigureAwait(false);
            if (counted is not long bytes)
            {
                return counted switch
                {
                    Unavailable error => error,
                    long => throw new System.Diagnostics.UnreachableException(),
                };
            }

            used = bytes;
        }
        catch (QueryCapacityException)
        {
            return StorageQuota.AccountingUnavailable;
        }
        if (used > _limit || delta > _limit - used)
        {
            return new QuotaExceeded("the organization storage quota would be exceeded", "storage_quota");
        }

        return new Done();
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
    private readonly Func<string, CancellationToken, ValueTask<StorageQuotaBytesResult>> _measure;
    private int _disposed;

    internal StorageQuotaGroupLease(SemaphoreSlim[] gates, IReadOnlyDictionary<string, long> limits,
        Func<string, CancellationToken, ValueTask<StorageQuotaBytesResult>> measure)
    {
        _gates = gates;
        _limits = limits;
        _measure = measure;
    }

    /// <summary>Whether the gated live plan has a byte ceiling; enterprise copies need no accounting scan.</summary>
    public bool RequiresAccounting(string organizationId) => _limits[organizationId] != long.MaxValue;

    /// <summary>Checks net staged growth before taking the system catalog writer; reducing changes remain available.</summary>
    public async ValueTask<StorageQuotaCheck> CheckAsync(string organizationId, long delta, CancellationToken cancellationToken)
    {
        var limit = _limits[organizationId];
        if (delta <= 0 || limit == long.MaxValue)
        {
            return new Done();
        }

        long used;
        try
        {
            var counted = await _measure(organizationId, cancellationToken).ConfigureAwait(false);
            if (counted is not long bytes)
            {
                return counted switch
                {
                    Unavailable error => error,
                    long => throw new System.Diagnostics.UnreachableException(),
                };
            }

            used = bytes;
        }
        catch (QueryCapacityException)
        {
            return StorageQuota.AccountingUnavailable;
        }
        if (used > limit || delta > limit - used)
        {
            return new QuotaExceeded("the organization storage quota would be exceeded", "storage_quota");
        }

        return new Done();
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

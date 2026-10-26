using System.Diagnostics.Metrics;
using TerraBase.Features.Qos;
using TerraBase.Tenancy;

namespace TerraBase.Features.Memory;

/// <summary>A process limit with organization admission bounded by each trusted plan.</summary>
internal sealed class MemoryBudget : IMemoryBudget
{
    private const int MaxTenants = 4096;
    private const int MaxReservations = 65_536;
    private const int MaxOrganizationChars = 256;
    private readonly long _limit;
    // ponytail: one gate keeps process and tenant accounting atomic; split only after measuring contention.
    private readonly Lock _gate = new();
    private readonly Dictionary<string, TenantUsage> _tenants = [];
    private long _reserved;
    private int _reservations;

    public MemoryBudget(long limit, Meter meter)
    {
        _limit = limit;
        meter.CreateObservableGauge("terrabase.memory.reserved", () => Volatile.Read(ref _reserved), "By", "Bytes reserved from memory-budget.");
    }

    public IMemoryReservation? TryReserve(MemoryLane lane, long bytes) => TryReserve(lane, bytes, QosContext.Current);

    public IMemoryReservation? TryReserve(MemoryLane lane, long bytes, TenantContext? tenant)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bytes);
        var share = tenant is null ? 1 : QosPolicy.For(tenant.Plan).MemoryShare;
        var tenantLimit = share == 1 ? _limit : (long)(_limit * share);
        var tenantReservations = Math.Min(MaxReservations - 4096, (int)(MaxReservations * share));
        if (bytes == 0)
        {
            // Zero-byte leases retain no tenant or budget state, even when capacity is exhausted.
            return EmptyReservation.Instance;
        }

        lock (_gate)
        {
            if (bytes > _limit - _reserved || _reservations == MaxReservations)
            {
                return null;
            }

            if (tenant is not null)
            {
                var known = _tenants.TryGetValue(tenant.OrganizationId, out var reserved);
                if (tenant.OrganizationId.Length > MaxOrganizationChars
                    || (!known && _tenants.Count == MaxTenants)
                    || reserved.Reservations >= tenantReservations
                    || bytes > tenantLimit - reserved.Bytes)
                {
                    return null;
                }

                _tenants[tenant.OrganizationId] = new TenantUsage(reserved.Bytes + bytes, reserved.Reservations + 1);
            }

            _reserved += bytes;
            _reservations++;
            return new Reservation(this, bytes, tenant?.OrganizationId, tenantLimit);
        }
    }

    public bool TryGrow(IMemoryReservation reservation, long bytes) => reservation is Reservation owned && owned.TryGrow(this, bytes);

    private sealed class Reservation(MemoryBudget budget, long bytes, string? organization, long tenantLimit) : IMemoryReservation
    {
        private long _bytes = bytes;
        private int _released;

        public long Bytes => Volatile.Read(ref _bytes);

        public bool TryGrow(MemoryBudget issuer, long more)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(more);
            lock (budget._gate)
            {
                // Growth takes no table entry, so both byte limits are the whole admission.
                if (issuer != budget || Volatile.Read(ref _released) != 0 || more > budget._limit - budget._reserved)
                {
                    return false;
                }

                if (organization is not null)
                {
                    var current = budget._tenants[organization];
                    if (more > tenantLimit - current.Bytes)
                    {
                        return false;
                    }

                    budget._tenants[organization] = current with { Bytes = current.Bytes + more };
                }

                budget._reserved += more;
                _bytes += more;
                return true;
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0)
            {
                return;
            }

            lock (budget._gate)
            {
                budget._reserved -= _bytes;
                budget._reservations--;
                if (organization is not null)
                {
                    var current = budget._tenants[organization];
                    var remaining = new TenantUsage(current.Bytes - _bytes, current.Reservations - 1);
                    if (remaining.Reservations == 0)
                    {
                        budget._tenants.Remove(organization);
                    }
                    else
                    {
                        budget._tenants[organization] = remaining;
                    }
                }
            }
        }
    }

    private readonly record struct TenantUsage(long Bytes, int Reservations);

    private sealed class EmptyReservation : IMemoryReservation
    {
        public static EmptyReservation Instance { get; } = new();

        public long Bytes => 0;

        public void Dispose()
        {
        }
    }
}

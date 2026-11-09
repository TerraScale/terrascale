using TerraBase.Features.Memory;
using TerraBase.Tenancy;

namespace TerraBase.Features.Shapes;

internal sealed class ShapeConnections(IMemoryBudget memory)
{
    internal const int PerTenant = 1024;
    private const int Capacity = 4096;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, int> _tenants = [];
    private int _active;

    internal IDisposable? TryEnter(TenantContext tenant)
    {
        lock (_gate)
        {
            var active = _tenants.GetValueOrDefault(tenant.OrganizationId);
            if (_active >= Capacity || active >= PerTenant || memory.TryReserve(4096, tenant) is not { } reservation)
            {
                return null;
            }

            _active++;
            _tenants[tenant.OrganizationId] = active + 1;
            return new Lease(this, tenant.OrganizationId, reservation);
        }
    }

    private void Leave(string tenant)
    {
        lock (_gate)
        {
            _active--;
            if (--_tenants[tenant] == 0)
            {
                _tenants.Remove(tenant);
            }
        }
    }

    private sealed class Lease(ShapeConnections owner, string tenant, IMemoryReservation reservation) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                owner.Leave(tenant);
                reservation.Dispose();
            }
        }
    }
}

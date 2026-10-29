using TerraBase.Tenancy;

namespace TerraBase.Engine;

public interface IShipperLeases
{
    ValueTask<ulong?> ShipperEpochAsync(TenantContext? database, CancellationToken cancellationToken);
}

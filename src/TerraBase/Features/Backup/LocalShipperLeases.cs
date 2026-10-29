using TerraBase.Engine;
using TerraBase.Tenancy;

namespace TerraBase.Features.Backup;

internal sealed class LocalShipperLeases : IShipperLeases
{
    public ValueTask<ulong?> ShipperEpochAsync(TenantContext? database, CancellationToken cancellationToken) =>
        ValueTask.FromResult<ulong?>(1);
}

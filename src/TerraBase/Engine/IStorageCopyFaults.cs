using TerraBase.Tenancy;

namespace TerraBase.Engine;

internal interface IStorageCopyFaults
{
    ValueTask<bool> RefusedAsync(TenantContext database, Ulid incarnation, CancellationToken cancellationToken);
}

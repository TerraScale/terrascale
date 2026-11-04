using TerraBase.Tenancy;

namespace TerraBase.Engine;

internal interface IDatabaseRewind
{
    ValueTask<bool> RewindAsync(TenantContext database, TenantContext source, CancellationToken cancellationToken);
}

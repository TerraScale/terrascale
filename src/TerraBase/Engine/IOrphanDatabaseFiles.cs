using TerraBase.Tenancy;

namespace TerraBase.Engine;

internal interface IOrphanDatabaseFiles
{
    ValueTask<bool?> IsBusyAsync(TenantContext database, CancellationToken cancellationToken);
}

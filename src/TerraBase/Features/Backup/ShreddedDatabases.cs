using TerraBase.Tenancy;

namespace TerraBase.Features.Backup;

internal interface IShreddedDatabases
{
    ValueTask<bool> ObserveShreddedAsync(TenantContext database, DateTimeOffset deletedAt, CancellationToken token);

    Task<bool> EraseShreddedAsync(TenantContext database, CancellationToken token);
}

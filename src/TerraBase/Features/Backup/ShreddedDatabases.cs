using TerraBase.Errors;
using TerraBase.Tenancy;

namespace TerraBase.Features.Backup;

internal readonly union ObserveShreddedResult(bool, Forbidden);

internal interface IShreddedDatabases
{
    ValueTask<ObserveShreddedResult> ObserveShreddedAsync(TenantContext database, DateTimeOffset deletedAt, CancellationToken token);

    Task<bool> EraseShreddedAsync(TenantContext database, CancellationToken token);
}

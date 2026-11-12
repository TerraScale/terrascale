using TerraBase.Errors;
using TerraBase.Features.Platform;
using TerraBase.Tenancy;

namespace TerraBase.Features.Backup;

internal readonly union ObserveShreddedResult(bool, Forbidden);

internal static class ShredGuard
{
    internal static Forbidden? Refusal(TenantContext database) => PlatformDatabases.IsPlatformDatabase(database.DatabaseId)
        ? new Forbidden("a platform database is never shredded")
        : null;
}

internal interface IShreddedDatabases
{
    ValueTask<ObserveShreddedResult> ObserveShreddedAsync(TenantContext database, DateTimeOffset deletedAt, CancellationToken token);

    Task<bool> EraseShreddedAsync(TenantContext database, CancellationToken token);
}

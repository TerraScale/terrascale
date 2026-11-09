using TerraBase.Tenancy;

namespace TerraBase.Features.Management;

internal interface IDatabaseFileCatalog
{
    ValueTask<RegisteredDatabaseResult> RegisteredFileAsync(TenantContext database, CancellationToken cancellationToken);
}

using TerraBase.Tenancy;

namespace TerraBase.Features.Management;

/// <summary>Catalog ownership of tenant files, including tracked backup snapshots.</summary>
internal interface IDatabaseFileCatalog
{
    /// <summary>True only for a registered project database or tracked snapshot in a readable catalog.</summary>
    ValueTask<RegisteredDatabaseResult> RegisteredFileAsync(TenantContext database, CancellationToken cancellationToken);
}

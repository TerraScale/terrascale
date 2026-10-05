using TerraScale.TerraBase.Tenancy;

namespace TerraScale.TerraBase.Features.Management.Usage;

/// <summary>What one database uses. Counters are best-effort totals since the database was created (dashboard usage, not billing).</summary>
/// <param name="DatabaseId">The database.</param>
/// <param name="StorageBytes">Bytes on disk.</param>
/// <param name="Collections">Collections in the database.</param>
/// <param name="Documents">Live documents stored.</param>
/// <param name="Reads">Document reads: gets, and list or query pages.</param>
/// <param name="Writes">Document writes: committed puts, patches and deletes, including each one of a transaction.</param>
public sealed record DatabaseUsage(string DatabaseId, long StorageBytes, long Collections, long Documents, long Reads, long Writes);

/// <summary>What every database of a project uses, summed.</summary>
/// <param name="ProjectId">The project.</param>
/// <param name="Databases">Databases in the project.</param>
/// <param name="StorageBytes">Bytes on disk.</param>
/// <param name="Documents">Documents stored.</param>
/// <param name="Reads">Document reads.</param>
/// <param name="Writes">Document writes.</param>
/// <param name="Items">Usage of each database, in creation order.</param>
public sealed record ProjectUsage(string ProjectId, long Databases, long StorageBytes, long Documents, long Reads, long Writes, IReadOnlyList<DatabaseUsage> Items);

/// <summary>Storage and operation usage of managed databases.</summary>
public interface IUsageService
{
    /// <summary>Reads the usage of <paramref name="database"/>; <see langword="null"/> when it was deleted meanwhile.</summary>
    ValueTask<DatabaseUsage?> GetDatabaseUsageAsync(TenantContext database, CancellationToken cancellationToken);

    /// <summary>Sums the usage of every database of a project; <see langword="null"/> when the project does not exist.</summary>
    ValueTask<ProjectUsage?> GetProjectUsageAsync(string userId, string organizationId, string projectId, CancellationToken cancellationToken);
}

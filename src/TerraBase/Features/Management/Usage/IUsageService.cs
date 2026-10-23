using TerraBase.Errors;
using TerraBase.Tenancy;

namespace TerraBase.Features.Management.Usage;

/// <summary>Reads and writes over a period.</summary>
/// <param name="From">Start of the first bucket.</param>
/// <param name="To">End of the last bucket, exclusive.</param>
/// <param name="Granularity"><c>hour</c> or <c>day</c>.</param>
/// <param name="Reads">Document reads in the period.</param>
/// <param name="Writes">Document writes in the period.</param>
public sealed record UsagePeriod(DateTimeOffset From, DateTimeOffset To, string Granularity, long Reads, long Writes);

/// <summary>Reads and writes of one bucket.</summary>
/// <param name="Start">Start of the bucket (UTC hour or day).</param>
/// <param name="Reads">Document reads.</param>
/// <param name="Writes">Document writes.</param>
public sealed record UsagePoint(DateTimeOffset Start, long Reads, long Writes);

/// <summary>What one database uses. Counters are best-effort dashboard usage, not billing.</summary>
/// <param name="DatabaseId">The database.</param>
/// <param name="StorageBytes">Bytes on disk.</param>
/// <param name="LogicalBytes">Metered logical storage: JSON bytes of the live documents.</param>
/// <param name="Collections">Collections in the database.</param>
/// <param name="Documents">Live documents stored.</param>
/// <param name="Reads">Document reads since the database was created: gets, and list and query pages, and SQL runs.</param>
/// <param name="Writes">Document writes since the database was created: committed puts, patches and deletes, including each one of a transaction.</param>
/// <param name="Period">Reads and writes over the requested period.</param>
/// <param name="Series">One point per bucket of the period, oldest first; empty for an item of a project.</param>
public sealed record DatabaseUsage(
    string DatabaseId, long StorageBytes, long LogicalBytes, long Collections, long Documents, long Reads, long Writes, UsagePeriod Period, IReadOnlyList<UsagePoint> Series);

/// <summary>
/// Billed operation units of the current UTC month (https://terrascale.tech/pricing.json <c>metering.operationUnit</c>):
/// each started 4 KiB of a document read or written, summed over every region and node, including databases deleted
/// since. Replication is not billed. The plan allowance applies to <see cref="OrganizationUnits"/>.
/// </summary>
/// <param name="Month">The UTC month, <c>yyyy-MM</c>.</param>
/// <param name="ReadUnits">Read units of the project.</param>
/// <param name="WriteUnits">Write units of the project.</param>
/// <param name="Units">Read plus write units of the project.</param>
/// <param name="OrganizationUnits">Read plus write units of every project of the organization: what the monthly allowance counts.</param>
public sealed record UsageMonth(string Month, long ReadUnits, long WriteUnits, long Units, long OrganizationUnits);

/// <summary>What every database of a project uses, summed.</summary>
/// <param name="ProjectId">The project.</param>
/// <param name="Databases">Databases in the project.</param>
/// <param name="StorageBytes">Bytes on disk.</param>
/// <param name="LogicalBytes">JSON bytes of the live documents.</param>
/// <param name="Documents">Documents stored.</param>
/// <param name="Reads">Document reads since each database was created.</param>
/// <param name="Writes">Document writes since each database was created.</param>
/// <param name="Period">Reads and writes over the requested period.</param>
/// <param name="Series">One point per bucket of the period, oldest first.</param>
/// <param name="Items">Usage of each database, in creation order.</param>
/// <param name="Month">Billed operation units this UTC month.</param>
public sealed record ProjectUsage(
    string ProjectId, long Databases, long StorageBytes, long LogicalBytes, long Documents, long Reads, long Writes, UsagePeriod Period,
    IReadOnlyList<UsagePoint> Series, IReadOnlyList<DatabaseUsage> Items, UsageMonth Month);

/// <summary>The usage of a database, or why there is none.</summary>
public readonly union DatabaseUsageResult(DatabaseUsage, NotFound, Validation, Unavailable);

/// <summary>The usage of a project, or why there is none.</summary>
public readonly union ProjectUsageResult(ProjectUsage, NotFound, Validation, Unavailable);

/// <summary>Storage and operation usage of managed databases.</summary>
public interface IUsageService
{
    /// <summary>Reads the usage of <paramref name="database"/>; <see cref="NotFound"/> when it was deleted meanwhile.</summary>
    ValueTask<DatabaseUsageResult> GetDatabaseUsageAsync(TenantContext database, UsageWindow window, CancellationToken cancellationToken);

    /// <summary>Sums the usage of every database of a project; <see cref="NotFound"/> when the project does not exist.</summary>
    ValueTask<ProjectUsageResult> GetProjectUsageAsync(string userId, string organizationId, string projectId, UsageWindow window, CancellationToken cancellationToken);
}

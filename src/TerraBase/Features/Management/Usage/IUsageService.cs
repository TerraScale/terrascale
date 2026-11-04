using TerraBase.Errors;
using TerraBase.Tenancy;

namespace TerraBase.Features.Management.Usage;

/// <summary>Reads and writes over a period.</summary>
/// <param name="From">Start of the first bucket.</param>
/// <param name="To">End of the last bucket, exclusive.</param>
/// <param name="Granularity"><c>hour</c> or <c>day</c>.</param>
/// <param name="Reads">Reads in the period.</param>
/// <param name="Writes">Writes in the period.</param>
public sealed record UsagePeriod(DateTimeOffset From, DateTimeOffset To, string Granularity, long Reads, long Writes);

/// <summary>Reads and writes of one bucket.</summary>
/// <param name="Start">Start of the bucket (UTC hour or day).</param>
/// <param name="Reads">Reads.</param>
/// <param name="Writes">Writes.</param>
public sealed record UsagePoint(DateTimeOffset Start, long Reads, long Writes);

/// <summary>What one database uses. Counters are best-effort dashboard usage, not billing.</summary>
/// <param name="DatabaseId">The database.</param>
/// <param name="StorageBytes">Bytes on disk.</param>
/// <param name="LogicalBytes">Logical bytes metered outside the SQL storage meter: 0 for a database, whose SQL storage is metered as <see cref="AllowanceUsage.StorageByteHours"/>.</param>
/// <param name="Reads">Reads since the database was created.</param>
/// <param name="Writes">Writes since the database was created.</param>
/// <param name="Period">Reads and writes over the requested period.</param>
/// <param name="Series">One point per bucket of the period, oldest first; empty for an item of a project.</param>
public sealed record DatabaseUsage(
    string DatabaseId, long StorageBytes, long LogicalBytes, long Reads, long Writes, UsagePeriod Period, IReadOnlyList<UsagePoint> Series);

/// <summary>
/// Operation units of the current UTC month (https://terrascale.tech/pricing.json <c>metering.operationUnit</c>), summed
/// over every region and node, including databases deleted since. Writes are billed; SQL reads count against fair use and
/// shape reads are not counted. The plan allowance and cap apply to <see cref="OrganizationUnits"/>.
/// </summary>
/// <param name="Month">The UTC month, <c>yyyy-MM</c>.</param>
/// <param name="ReadUnits">Read units of the project.</param>
/// <param name="WriteUnits">Write units of the project.</param>
/// <param name="Units">Billed write units of the project.</param>
/// <param name="OrganizationUnits">Billed write units of every project of the organization: what the monthly allowance and cap count.</param>
/// <param name="OrganizationReadUnits">Unbilled SQL read units of every project of the organization: what the monthly fair use counts.</param>
public sealed record UsageMonth(string Month, long ReadUnits, long WriteUnits, long Units, long OrganizationUnits, long OrganizationReadUnits);

/// <summary>What every database of a project uses, summed.</summary>
/// <param name="ProjectId">The project.</param>
/// <param name="Databases">Databases in the project.</param>
/// <param name="StorageBytes">Bytes on disk.</param>
/// <param name="LogicalBytes">The databases' <see cref="DatabaseUsage.LogicalBytes"/> plus the project's file bytes.</param>
/// <param name="Reads">Reads since each database was created.</param>
/// <param name="Writes">Writes since each database was created.</param>
/// <param name="Period">Reads and writes over the requested period.</param>
/// <param name="Series">One point per bucket of the period, oldest first.</param>
/// <param name="Items">Usage of each database, in creation order.</param>
/// <param name="Month">Billed operation units this UTC month.</param>
public sealed record ProjectUsage(
    string ProjectId, long Databases, long StorageBytes, long LogicalBytes, long Reads, long Writes, UsagePeriod Period,
    IReadOnlyList<UsagePoint> Series, IReadOnlyList<DatabaseUsage> Items, UsageMonth Month)
{
    /// <summary>Logical file bytes and pending reservations, counted once across regions.</summary>
    public long FileBytes { get; init; }
}

/// <summary>The usage of a database, or why there is none.</summary>
public readonly union DatabaseUsageResult(DatabaseUsage, NotFound, Validation, Unavailable);

/// <summary>The usage of a project, or why there is none.</summary>
public readonly union ProjectUsageResult(ProjectUsage, NotFound, Validation, Unavailable);

/// <summary>One organization's billed usage this UTC month.</summary>
/// <param name="OrganizationId">The organization.</param>
/// <param name="Operations">Write units of every project, region and node: what the allowance counts and overage bills.</param>
/// <param name="LogicalBytes">Not reported yet: null.</param>
/// <param name="SqlReads">SQL read units of every project, region and node: counted against fair use, never billed.</param>
/// <param name="StorageByteHours">Stored bytes times hours this month, sampled hourly on every node: the month's GB-months.</param>
public sealed record AllowanceUsage(string OrganizationId, long? Operations, long? LogicalBytes, long? SqlReads = null, long? StorageByteHours = null);

/// <summary>A page of organizations' billed usage this UTC month, in organization id order.</summary>
/// <param name="PeriodStart">The first instant of the UTC month.</param>
/// <param name="ObservedAt">When the page was read.</param>
/// <param name="Items">Up to 25 organizations after the requested one.</param>
/// <param name="Next">The last organization of the page when more follow; pass it as <c>after</c>.</param>
/// <param name="OperationsContract">What <see cref="AllowanceUsage.Operations"/> counts.</param>
/// <param name="StorageContract">What <see cref="AllowanceUsage.StorageByteHours"/> counts.</param>
public sealed record AllowanceUsagePage(
    DateTimeOffset PeriodStart, DateTimeOffset ObservedAt, IReadOnlyList<AllowanceUsage> Items, string? Next, string OperationsContract, string StorageContract);

/// <summary>A page of billed usage, or why there is none.</summary>
public readonly union AllowanceUsageResult(AllowanceUsagePage, Unavailable);

/// <summary>Storage and operation usage of managed databases.</summary>
public interface IUsageService
{
    /// <summary>Reads the usage of <paramref name="database"/>; <see cref="NotFound"/> when it was deleted meanwhile.</summary>
    ValueTask<DatabaseUsageResult> GetDatabaseUsageAsync(TenantContext database, UsageWindow window, CancellationToken cancellationToken);

    /// <summary>Sums the usage of every database of a project; <see cref="NotFound"/> when the project does not exist.</summary>
    ValueTask<ProjectUsageResult> GetProjectUsageAsync(string userId, string organizationId, string projectId, UsageWindow window, CancellationToken cancellationToken);

    /// <summary>Every organization's billed usage this month, or the previous one, 25 at a time after <paramref name="after"/>.</summary>
    ValueTask<AllowanceUsageResult> ListAllowanceUsageAsync(string? after, bool previous, CancellationToken cancellationToken);
}

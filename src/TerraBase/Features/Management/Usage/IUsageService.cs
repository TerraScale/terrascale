using TerraBase.Errors;
using TerraBase.Tenancy;

namespace TerraBase.Features.Management.Usage;

public sealed record UsagePeriod(DateTimeOffset From, DateTimeOffset To, string Granularity, long Reads, long Writes);

public sealed record UsagePoint(DateTimeOffset Start, long Reads, long Writes);

public sealed record DatabaseUsage(
    string DatabaseId, long StorageBytes, long LogicalBytes, long Reads, long Writes, UsagePeriod Period, IReadOnlyList<UsagePoint> Series);

// Operation units of the current UTC month: https://terrascale.tech/pricing.json metering.operationUnit.
public sealed record UsageMonth(string Month, long ReadUnits, long WriteUnits, long Units, long OrganizationUnits, long OrganizationReadUnits);

public sealed record ProjectUsage(
    string ProjectId, long Databases, long StorageBytes, long LogicalBytes, long Reads, long Writes, UsagePeriod Period,
    IReadOnlyList<UsagePoint> Series, IReadOnlyList<DatabaseUsage> Items, UsageMonth Month)
{
    public long FileBytes { get; init; }
}

public readonly union DatabaseUsageResult(DatabaseUsage, NotFound, Validation, Unavailable);

public readonly union ProjectUsageResult(ProjectUsage, NotFound, Validation, Unavailable);

public sealed record AllowanceUsage(string OrganizationId, long? Operations, long? LogicalBytes, long? SqlReads = null, long? StorageByteHours = null);

public sealed record AllowanceUsagePage(
    DateTimeOffset PeriodStart, DateTimeOffset ObservedAt, IReadOnlyList<AllowanceUsage> Items, string? Next, string OperationsContract, string StorageContract);

public readonly union AllowanceUsageResult(AllowanceUsagePage, Unavailable);

public interface IUsageService
{
    ValueTask<DatabaseUsageResult> GetDatabaseUsageAsync(TenantContext database, UsageWindow window, CancellationToken cancellationToken);

    ValueTask<ProjectUsageResult> GetProjectUsageAsync(string userId, string organizationId, string projectId, UsageWindow window, CancellationToken cancellationToken);

    ValueTask<AllowanceUsageResult> ListAllowanceUsageAsync(string? after, bool previous, CancellationToken cancellationToken);
}

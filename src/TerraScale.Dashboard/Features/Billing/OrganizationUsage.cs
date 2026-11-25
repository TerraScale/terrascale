using System.Globalization;
using TerraScale.Dashboard.Platform.Clients;

namespace TerraScale.Dashboard.Features.Billing;

public sealed record MeasuredUsage(string OrganizationId, DateTimeOffset Month, long OperationUnits, long StorageByteHours);

public sealed record PartialUsage(string OrganizationId, DateTimeOffset Month, long OperationUnits, long StorageByteHours, IReadOnlyList<string> Gaps)
{
    public bool OperationsComplete => Gaps.All(static gap => gap == OrganizationUsage.StorageIncomplete);
}

public sealed record UsageUnavailable(string OrganizationId, DateTimeOffset Month, string Reason);

public readonly union OrganizationUsageRead(MeasuredUsage, PartialUsage, UsageUnavailable);

public sealed class OrganizationUsage(TerraBaseHttpClient terraBase)
{
    public const int Concurrency = 4;

    public const string StorageIncomplete = "storage_sample_incomplete";

    public static DateTimeOffset MonthOf(DateTimeOffset value) => new(value.Year, value.Month, 1, 0, 0, 0, TimeSpan.Zero);

    public async Task<OrganizationUsageRead> ReadAsync(string organizationId, DateTimeOffset month, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(month, MonthOf(month));
        var outcome = await terraBase.GetOrganizationUsageAsync(organizationId, month, cancellationToken);
        return outcome switch
        {
            TerraBaseOrganizationUsage usage => Read(organizationId, month, usage),
            TerraBaseRefusal refusal => new UsageUnavailable(organizationId, month, refusal.Code),
        };
    }

    private static OrganizationUsageRead Read(string organizationId, DateTimeOffset month, TerraBaseOrganizationUsage usage)
    {
        if (usage.OrganizationId != organizationId || usage.Month != month.ToString("yyyy-MM", CultureInfo.InvariantCulture) || usage.Gaps is null ||
            usage.Totals is not { } totals || usage.Projects is null || totals.RowsWritten < 0 || totals.StorageByteHours < 0 ||
            usage.Projects.Any(static project => project is null || project.RowsWritten < 0 || project.StorageByteHours < 0) ||
            usage.Projects.Sum(static project => (decimal)project.RowsWritten) != totals.RowsWritten ||
            usage.Projects.Sum(static project => (decimal)project.StorageByteHours) != totals.StorageByteHours)
        {
            return new UsageUnavailable(organizationId, month, "invalid_usage");
        }

        return usage.Complete && usage.Gaps.Count == 0
            ? new MeasuredUsage(organizationId, month, totals.RowsWritten, totals.StorageByteHours)
            : new PartialUsage(organizationId, month, totals.RowsWritten, totals.StorageByteHours, usage.Gaps.Count == 0 ? ["incomplete"] : usage.Gaps);
    }
}

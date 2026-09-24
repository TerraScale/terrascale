using TerraScale.Dashboard;

namespace TerraScale.Dashboard.Features.Admin.Pages.Usage;

public sealed record AdminUsagePageModel(
    DashboardStore.AdminPlatformUsage Overview,
    IReadOnlyList<DashboardStore.AdminUsageMetric> UsageRows,
    int Page,
    int TotalPages,
    int TotalRows) : IPageModel
{
    public ShellModel Shell { get; init; } = null!;
}

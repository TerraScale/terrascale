using TerraScale.Dashboard;

namespace TerraScale.Dashboard.Features.Admin.Pages.Usage;

public sealed record AdminUsagePageModel(DashboardStore.AdminPlatformUsage Overview) : IPageModel
{
    public ShellModel Shell { get; init; } = null!;
}

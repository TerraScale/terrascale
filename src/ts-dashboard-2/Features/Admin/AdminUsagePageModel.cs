using TerraScale.Dashboard2;

namespace TerraScale.Dashboard2.Features.Admin;

public sealed record AdminUsagePageModel(DashboardStore.AdminPlatformUsage Overview) : IPageModel
{
    public ShellModel Shell { get; init; } = null!;
}

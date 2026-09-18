using TerraScale.Dashboard2;

namespace TerraScale.Dashboard2.Features.Admin;

public sealed record AdminPricingPageModel(
    string Query,
    IReadOnlyList<DashboardStore.AdminPlanRow> Plans,
    string? Notice) : IPageModel
{
    public ShellModel Shell { get; init; } = null!;
}

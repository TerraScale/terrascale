using TerraScale.Dashboard;

namespace TerraScale.Dashboard.Features.Admin.Pages.Pricing;

public sealed record AdminPricingPageModel(
    string Query,
    IReadOnlyList<DashboardStore.AdminPlanRow> Plans,
    string? Notice) : IPageModel
{
    public ShellModel Shell { get; init; } = null!;
}

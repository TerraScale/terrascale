using TerraScale.Dashboard;

namespace TerraScale.Dashboard.Features.Admin.Pages.Pricing;

public sealed record AdminPricingPageModel(
    string Tab,
    string Query,
    IReadOnlyList<DashboardStore.AdminPlanRow> Plans,
    IReadOnlyList<DashboardStore.AdminPlanRow> PlanRows,
    int Page,
    int TotalPages,
    int TotalRows,
    string? Notice) : IPageModel
{
    public ShellModel Shell { get; init; } = null!;
}

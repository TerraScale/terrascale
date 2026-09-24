using TerraScale.Dashboard;

namespace TerraScale.Dashboard.Features.Admin.Pages.Organizations;

public sealed record AdminOrganizationsPageModel(
    string Query,
    IReadOnlyList<DashboardStore.AdminOrganizationRow> Rows,
    int WithOwners,
    int Page,
    int TotalPages,
    int TotalRows) : IPageModel
{
    public ShellModel Shell { get; init; } = null!;
}

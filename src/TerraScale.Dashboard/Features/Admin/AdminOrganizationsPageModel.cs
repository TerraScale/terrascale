using TerraScale.Dashboard;

namespace TerraScale.Dashboard.Features.Admin;

public sealed record AdminOrganizationsPageModel(
    string Query,
    IReadOnlyList<DashboardStore.AdminOrganizationRow> Rows,
    int WithOwners) : IPageModel
{
    public ShellModel Shell { get; init; } = null!;
}

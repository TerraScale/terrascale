using TerraScale.Dashboard2;

namespace TerraScale.Dashboard2.Features.Admin;

public sealed record AdminOrganizationsPageModel(
    string Query,
    IReadOnlyList<DashboardStore.AdminOrganizationRow> Rows,
    int WithOwners) : IPageModel
{
    public ShellModel Shell { get; init; } = null!;
}

using TerraScale.Dashboard;

namespace TerraScale.Dashboard.Features.Admin;

public sealed record AdminUsersPageModel(
    string Query,
    IReadOnlyList<DashboardStore.AdminUserRow> Rows) : IPageModel
{
    public ShellModel Shell { get; init; } = null!;
}

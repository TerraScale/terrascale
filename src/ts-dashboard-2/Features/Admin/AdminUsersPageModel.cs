using TerraScale.Dashboard2;

namespace TerraScale.Dashboard2.Features.Admin;

public sealed record AdminUsersPageModel(
    string Query,
    IReadOnlyList<DashboardStore.AdminUserRow> Rows) : IPageModel
{
    public ShellModel Shell { get; init; } = null!;
}

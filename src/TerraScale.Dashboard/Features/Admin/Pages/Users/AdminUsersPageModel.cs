using TerraScale.Dashboard;

namespace TerraScale.Dashboard.Features.Admin.Pages.Users;

public sealed record AdminUsersPageModel(
    string Query,
    IReadOnlyList<DashboardStore.AdminUserRow> Rows,
    int EnabledRows,
    int OwnerRows,
    int Page,
    int TotalPages,
    int TotalRows) : IPageModel
{
    public ShellModel Shell { get; init; } = null!;
}

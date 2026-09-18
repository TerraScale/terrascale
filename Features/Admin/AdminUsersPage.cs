using TerraScale.Dashboard2;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard2.Features.Admin;

public sealed record AdminUsersPageModel : IPageModel
{
    public ShellModel Shell { get; init; } = null!;
}

public sealed class AdminUsersPageEndpoint(DashboardStore store)
    : SlicePageEndpoint<AdminUsersSlice, AdminUsersPageModel>(store, "/admin/users")
{
    protected override AdminUsersPageModel Load() => new() { Shell = ShellFactory.Create(Store, PageRoute) };
}

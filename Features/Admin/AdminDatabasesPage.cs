using TerraScale.Dashboard2;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard2.Features.Admin;

public sealed record AdminDatabasesPageModel : IPageModel
{
    public ShellModel Shell { get; init; } = null!;
}

public sealed class AdminDatabasesPageEndpoint(DashboardStore store)
    : SlicePageEndpoint<AdminDatabasesSlice, AdminDatabasesPageModel>(store, "/admin/databases")
{
    protected override AdminDatabasesPageModel Load() => new() { Shell = ShellFactory.Create(Store, PageRoute) };
}

using TerraScale.Dashboard2;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard2.Features.Admin;

public sealed record AdminHomePageModel : IPageModel
{
    public ShellModel Shell { get; init; } = null!;
}

public sealed class AdminHomePageEndpoint(DashboardStore store)
    : SlicePageEndpoint<AdminHomeSlice, AdminHomePageModel>(store, "/admin")
{
    protected override AdminHomePageModel Load() => new() { Shell = ShellFactory.Create(Store, PageRoute) };
}

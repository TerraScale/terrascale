using TerraScale.Dashboard2;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard2.Features.Admin;

public sealed record AdminUsagePageModel : IPageModel
{
    public ShellModel Shell { get; init; } = null!;
}

public sealed class AdminUsagePageEndpoint(DashboardStore store)
    : SlicePageEndpoint<AdminUsageSlice, AdminUsagePageModel>(store, "/admin/usage")
{
    protected override AdminUsagePageModel Load() => new() { Shell = ShellFactory.Create(Store, PageRoute) };
}

using TerraScale.Dashboard;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Admin;

public sealed class AdminUsagePageEndpoint(DashboardStore store)
    : SlicePageEndpoint<AdminUsageSlice, AdminUsagePageModel>(store, "/admin/usage")
{
    protected override AdminUsagePageModel Load() =>
        new(Store.AdminUsage) { Shell = ShellFactory.Create(Store, PageRoute) };
}

using TerraScale.Dashboard;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Admin.Pages.Usage;

public sealed class AdminUsagePageEndpoint(DashboardStore store)
    : SlicePageEndpoint<AdminUsageSlice, AdminUsagePageModel>(store, "/admin/usage")
{
    protected override AdminUsagePageModel Load()
    {
        var usage = Store.AdminUsage;
        var page = AdminPresentation.Paginate(usage.Usage, HttpContext.Request.Query["page"].FirstOrDefault());
        return new AdminUsagePageModel(usage, page.Rows, page.Number, page.TotalPages, page.TotalRows)
        {
            Shell = ShellFactory.Create(Store, PageRoute),
        };
    }
}

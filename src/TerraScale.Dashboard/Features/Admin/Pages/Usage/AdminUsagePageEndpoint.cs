using TerraScale.Dashboard;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Admin.Pages.Usage;

public sealed class AdminUsagePageEndpoint(DashboardStore store)
    : SlicePageEndpoint<AdminUsageSlice, AdminUsagePageModel>(store, "/admin/usage")
{
    protected override AdminUsagePageModel Load()
    {
        var page = AdminPresentation.Paginate(Store.AdminUsage.Usage, HttpContext.Request.Query["page"].FirstOrDefault());
        return new AdminUsagePageModel(Store.AdminUsage, page.Rows, page.Number, page.TotalPages, page.TotalRows)
        {
            Shell = ShellFactory.Create(Store, PageRoute),
        };
    }
}

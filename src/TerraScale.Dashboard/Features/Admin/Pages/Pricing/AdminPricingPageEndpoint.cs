using TerraScale.Dashboard;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Admin.Pages.Pricing;

public sealed class AdminPricingPageEndpoint(DashboardStore store)
    : SlicePageEndpoint<AdminPricingSlice, AdminPricingPageModel>(store, "/admin/pricing")
{
    protected override AdminPricingPageModel Load()
    {
        var tab = HttpContext.Request.Query["tab"].FirstOrDefault() is "create" ? "create" : "plans";
        var query = HttpContext.Request.Query["q"].FirstOrDefault() ?? "";
        var notice = HttpContext.Request.Query["notice"].FirstOrDefault();
        var plans = AdminPresentation.FilterRows(
            [.. Store.AdminPlans], query, plan => $"{plan.PlanId} {plan.Name}");
        var page = AdminPresentation.Paginate(plans, HttpContext.Request.Query["page"].FirstOrDefault());
        return new AdminPricingPageModel(tab, query, plans, page.Rows, page.Number, page.TotalPages, page.TotalRows, notice)
        {
            Shell = ShellFactory.Create(Store, PageRoute),
        };
    }
}

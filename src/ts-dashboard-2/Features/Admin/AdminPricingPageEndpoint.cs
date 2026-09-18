using TerraScale.Dashboard2;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard2.Features.Admin;

public sealed class AdminPricingPageEndpoint(DashboardStore store)
    : SlicePageEndpoint<AdminPricingSlice, AdminPricingPageModel>(store, "/admin/pricing")
{
    protected override AdminPricingPageModel Load()
    {
        var query = HttpContext.Request.Query["q"].FirstOrDefault() ?? "";
        var notice = HttpContext.Request.Query["notice"].FirstOrDefault();
        var plans = AdminPresentation.FilterRows(
            [.. Store.AdminPlans], query, plan => $"{plan.PlanId} {plan.Name}");
        return new AdminPricingPageModel(query, plans, notice)
        {
            Shell = ShellFactory.Create(Store, PageRoute),
        };
    }
}

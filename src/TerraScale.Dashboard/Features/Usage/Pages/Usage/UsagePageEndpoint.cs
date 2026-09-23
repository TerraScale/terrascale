using TerraScale.Dashboard;

namespace TerraScale.Dashboard.Features.Usage.Pages.Usage;

public sealed class UsagePageEndpoint(DashboardStore store, StripePaymentOptions stripe) : SlicePageEndpoint<UsageSlice, UsagePageModel>(store, "/usage")
{
    protected override UsagePageModel Load() =>
        UsagePageModel.From(
            Store,
            stripe,
            PageRoute,
            HttpContext.Request.Query["range"].FirstOrDefault(),
            HttpContext.Request.Query["billing"].FirstOrDefault(),
            HttpContext.Request.Query["saved"].FirstOrDefault(),
            HttpContext.Request.Query["tab"].FirstOrDefault(),
            HttpContext.Request.Query["project"].FirstOrDefault(),
            HttpContext.Request.Query["database"].FirstOrDefault(),
            HttpContext.Request.Query["status"].FirstOrDefault(),
            HttpContext.Request.Query["issued"].FirstOrDefault(),
            int.TryParse(HttpContext.Request.Query["page"].FirstOrDefault(), out var page) ? page : 1);
}

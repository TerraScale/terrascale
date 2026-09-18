using TerraScale.Dashboard2;

namespace TerraScale.Dashboard2.Features.Usage;

public sealed class UsagePageEndpoint(DashboardStore store) : SlicePageEndpoint<UsageSlice, UsagePageModel>(store, "/usage")
{
    protected override UsagePageModel Load() =>
        UsagePageModel.From(
            Store,
            PageRoute,
            HttpContext.Request.Query["range"].FirstOrDefault(),
            HttpContext.Request.Query["billing"].FirstOrDefault());
}

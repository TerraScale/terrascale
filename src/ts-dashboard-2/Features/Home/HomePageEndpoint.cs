using TerraScale.Dashboard2;

namespace TerraScale.Dashboard2.Features.Home;

public sealed class HomePageEndpoint(DashboardStore store)
    : SlicePageEndpoint<HomeSlice, HomePageModel>(store, "/")
{
    protected override HomePageModel Load() =>
        HomePageModel.From(Store, PageRoute, HttpContext.Request.Query["range"].ToString());
}

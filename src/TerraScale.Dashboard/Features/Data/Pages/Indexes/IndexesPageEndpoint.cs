using TerraScale.Dashboard;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Data.Pages.Indexes;

public sealed class IndexesPageEndpoint(DashboardStore store)
    : SlicePageEndpoint<IndexesSlice, IndexesPageModel>(store, "/indexes")
{
    protected override IndexesPageModel Load() => IndexesPageModel.From(
        Store,
        collection: HttpContext.Request.Query["collection"].ToString(),
        actionError: HttpContext.Request.Query["applyError"].ToString() is { Length: > 0 } e ? e : null,
        tab: HttpContext.Request.Query["tab"].ToString(),
        page: int.TryParse(HttpContext.Request.Query["page"].ToString(), out var requestedPage) ? requestedPage : 1);
}

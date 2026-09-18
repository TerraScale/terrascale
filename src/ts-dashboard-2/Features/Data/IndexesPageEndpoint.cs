using TerraScale.Dashboard2;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard2.Features.Data;

public sealed class IndexesPageEndpoint(DashboardStore store)
    : SlicePageEndpoint<IndexesSlice, IndexesPageModel>(store, "/indexes")
{
    protected override IndexesPageModel Load() => IndexesPageModel.From(Store);
}

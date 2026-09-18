using TerraScale.Dashboard2;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard2.Features.Data;

public sealed class HistoryPageEndpoint(DashboardStore store)
    : SlicePageEndpoint<HistorySlice, HistoryPageModel>(store, "/history")
{
    protected override HistoryPageModel Load() => HistoryPageModel.From(Store);
}

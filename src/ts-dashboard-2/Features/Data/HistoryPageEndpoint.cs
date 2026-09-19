using TerraScale.Dashboard2;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard2.Features.Data;

public sealed class HistoryPageEndpoint(DashboardStore store)
    : SlicePageEndpoint<HistorySlice, HistoryPageModel>(store, "/history")
{
    protected override HistoryPageModel Load()
    {
        // v1: with database/collection/document all present the producer is still
        // unreachable, so the page swaps the validation message for the unavailable one.
        var query = HttpContext.Request.Query;
        var complete = new[] { "database", "collection", "document" }
            .All(name => query[name].ToString().Trim().Length > 0);
        return HistoryPageModel.From(
            Store,
            message: complete ? "Document history is temporarily unavailable." : null);
    }
}

using TerraScale.Dashboard;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Data.Pages.Explorer;

public sealed class DataPageEndpoint(DashboardStore store) : SlicePageEndpoint<DataSlice, DataPageModel>(store, "/data")
{
    protected override DataPageModel Load()
    {
        var query = HttpContext.Request.Query;
        // The saved-query picker GETs ?savedQueryId=; resolve it to its SQL text.
        var sql = query["sql"].ToString();
        if (sql.Length == 0 && query["savedQueryId"].ToString() is { Length: > 0 } id)
        {
            sql = Store.SavedQueries.FirstOrDefault(q => q.Id == id)?.Query ?? "";
        }

        return DataPageModel.From(
            Store,
            view: query["view"].ToString() is { Length: > 0 } v ? v : "queries",
            database: query["database"].ToString(),
            collection: query["collection"].ToString(),
            edit: query["edit"].ToString(),
            delete: query["delete"].ToString(),
            conflict: query["conflict"].ToString() == "1",
            error: query["error"].ToString() is { Length: > 0 } e ? e : null,
            notice: query["notice"].ToString() is { Length: > 0 } n ? n : null,
            sql: sql.Length > 0 ? sql : null,
            sqlCollection: query["collection"].ToString(),
            sqlChecked: query["checked"].ToString() == "1");
    }
}

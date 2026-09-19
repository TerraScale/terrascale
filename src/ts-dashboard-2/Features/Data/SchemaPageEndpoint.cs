using TerraScale.Dashboard2;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard2.Features.Data;

public sealed class SchemaPageEndpoint(DashboardStore store) : SlicePageEndpoint<SchemaSlice, SchemaPageModel>(store, "/schema")
{
    protected override SchemaPageModel Load()
    {
        var query = HttpContext.Request.Query;
        return SchemaPageModel.From(
            Store,
            collection: query["collection"].ToString(),
            showDiff: query["diff"].ToString() == "1",
            applied: query["applied"].ToString() == "1",
            loadError: query["error"].ToString() is { Length: > 0 } e ? e : null,
            draftError: query["schemaError"].ToString() is { Length: > 0 } d ? d : null);
    }
}

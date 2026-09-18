using TerraScale.Dashboard2;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard2.Features.Data;

public sealed class SchemaPageEndpoint(DashboardStore store) : SlicePageEndpoint<SchemaSlice, SchemaPageModel>(store, "/schema")
{
    protected override SchemaPageModel Load() => SchemaPageModel.From(
        Store,
        showDiff: HttpContext.Request.Query["diff"].ToString() == "1",
        error: HttpContext.Request.Query["schemaError"].ToString() is { Length: > 0 } e ? e : null);
}

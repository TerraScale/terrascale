using TerraScale.Dashboard2;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard2.Features.Audit;

public sealed class AuditPageEndpoint(DashboardStore store) : SlicePageEndpoint<AuditSlice, AuditPageModel>(store, "/audit")
{
    protected override AuditPageModel Load()
    {
        var query = HttpContext.Request.Query;
        return AuditPageModel.From(
            Store,
            PageRoute,
            query["search"].FirstOrDefault(),
            query["action"].FirstOrDefault(),
            query["severity"].FirstOrDefault(),
            query["range"].FirstOrDefault());
    }
}

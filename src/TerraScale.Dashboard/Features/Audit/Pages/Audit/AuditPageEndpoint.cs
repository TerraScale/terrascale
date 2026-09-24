using TerraScale.Dashboard;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Audit.Pages.Audit;

public sealed class AuditPageEndpoint(DashboardStore store) : SlicePageEndpoint<AuditSlice, AuditPageModel>(store, "/audit")
{
    protected override AuditPageModel Load()
    {
        var query = HttpContext.Request.Query;
        var page = int.TryParse(query["page"].FirstOrDefault(), out var parsedPage) ? parsedPage : 1;
        return AuditPageModel.From(
            Store,
            PageRoute,
            query["search"].FirstOrDefault(),
            query["action"].FirstOrDefault(),
            query["severity"].FirstOrDefault(),
            query["range"].FirstOrDefault(),
            page);
    }
}

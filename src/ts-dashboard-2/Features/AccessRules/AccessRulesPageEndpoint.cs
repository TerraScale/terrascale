using TerraScale.Dashboard2;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard2.Features.AccessRules;

public sealed class AccessRulesPageEndpoint(DashboardStore store)
    : SlicePageEndpoint<AccessRulesSlice, AccessRulesPageModel>(store, "/access-rules")
{
    protected override AccessRulesPageModel Load()
    {
        var query = HttpContext.Request.Query;
        return AccessRulesPageModel.From(
            Store,
            query["edit"].FirstOrDefault(),
            query["validate"].Count > 0,
            query["dryrun"].Count > 0,
            query["error"].FirstOrDefault())
            with { Shell = ShellFactory.Create(Store, PageRoute) };
    }
}

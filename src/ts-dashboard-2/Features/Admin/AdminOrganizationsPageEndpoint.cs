using TerraScale.Dashboard2;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard2.Features.Admin;

public sealed class AdminOrganizationsPageEndpoint(DashboardStore store)
    : SlicePageEndpoint<AdminOrganizationsSlice, AdminOrganizationsPageModel>(store, "/admin/organizations")
{
    protected override AdminOrganizationsPageModel Load()
    {
        var query = HttpContext.Request.Query["q"].FirstOrDefault() ?? "";
        var rows = AdminPresentation.FilterRows(
            [.. Store.AdminOrganizations], query, row => $"{row.OrganizationId} {row.Name} {row.Status}");
        return new AdminOrganizationsPageModel(
            query,
            rows,
            Store.AdminOrganizations.Count(o => o.OwnerCount > 0))
        {
            Shell = ShellFactory.Create(Store, PageRoute),
        };
    }
}

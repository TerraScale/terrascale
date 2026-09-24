using TerraScale.Dashboard;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Admin.Pages.Organizations;

public sealed class AdminOrganizationsPageEndpoint(DashboardStore store)
    : SlicePageEndpoint<AdminOrganizationsSlice, AdminOrganizationsPageModel>(store, "/admin/organizations")
{
    protected override AdminOrganizationsPageModel Load()
    {
        var query = HttpContext.Request.Query["q"].FirstOrDefault() ?? "";
        var rows = AdminPresentation.FilterRows(
            [.. Store.AdminOrganizations], query, row => $"{row.OrganizationId} {row.Name} {row.Status}");
        var page = AdminPresentation.Paginate(rows, HttpContext.Request.Query["page"].FirstOrDefault());
        return new AdminOrganizationsPageModel(
            query,
            page.Rows,
            Store.AdminOrganizations.Count(o => o.OwnerCount > 0),
            page.Number,
            page.TotalPages,
            page.TotalRows)
        {
            Shell = ShellFactory.Create(Store, PageRoute),
        };
    }
}

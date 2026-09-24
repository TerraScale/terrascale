using TerraScale.Dashboard;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Admin.Pages.Users;

public sealed class AdminUsersPageEndpoint(DashboardStore store)
    : SlicePageEndpoint<AdminUsersSlice, AdminUsersPageModel>(store, "/admin/users")
{
    protected override AdminUsersPageModel Load()
    {
        var query = (HttpContext.Request.Query["q"].FirstOrDefault() ?? "").Length > 200
            ? HttpContext.Request.Query["q"].FirstOrDefault()![..200]
            : HttpContext.Request.Query["q"].FirstOrDefault() ?? "";
        var rows = AdminPresentation.FilterRows(
            [.. Store.AdminUsers], query, row => $"{row.UserId} {row.DisplayName} {row.Email}");
        var page = AdminPresentation.Paginate(rows, HttpContext.Request.Query["page"].FirstOrDefault());
        return new AdminUsersPageModel(query, page.Rows, rows.Count(row => row.Enabled), rows.Count(row => row.Owner), page.Number, page.TotalPages, page.TotalRows)
        {
            Shell = ShellFactory.Create(Store, PageRoute),
        };
    }
}

using TerraScale.Dashboard2;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard2.Features.Admin;

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
        return new AdminUsersPageModel(query, rows) { Shell = ShellFactory.Create(Store, PageRoute) };
    }
}

using TerraScale.Dashboard;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Admin.Pages.Databases;

public sealed class AdminDatabasesPageEndpoint(DashboardStore store)
    : SlicePageEndpoint<AdminDatabasesSlice, AdminDatabasesPageModel>(store, "/admin/databases")
{
    protected override AdminDatabasesPageModel Load()
    {
        var query = HttpContext.Request.Query["q"].FirstOrDefault() ?? "";
        List<AdminDatabaseRow> rows = [.. Store.Databases.Select(d => new AdminDatabaseRow(d.Id, d.Name, RegionsOf(d), d.Version, d.CreatedAt))];
        var filtered = AdminPresentation.FilterRows(
            rows, query, row => $"{row.DatabaseId} {row.Name} {row.Version} {string.Join(' ', row.Regions)}");
        var page = AdminPresentation.Paginate(filtered, HttpContext.Request.Query["page"].FirstOrDefault());
        return new AdminDatabasesPageModel(
            query,
            page.Rows,
            rows.SelectMany(r => r.Regions).Distinct().Count(),
            page.Number,
            page.TotalPages,
            page.TotalRows)
        {
            Shell = ShellFactory.Create(Store, PageRoute),
        };
    }

    private static IReadOnlyList<string> RegionsOf(DashboardDatabase database)
    {
        List<string> regions = [database.PrimaryRegion];
        if (database.BackupRegion is { } backup)
        {
            regions.Add(backup);
        }

        return regions;
    }
}

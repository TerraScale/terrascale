using TerraScale.Dashboard2;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard2.Features.Admin;

public sealed class AdminDatabasesPageEndpoint(DashboardStore store)
    : SlicePageEndpoint<AdminDatabasesSlice, AdminDatabasesPageModel>(store, "/admin/databases")
{
    protected override AdminDatabasesPageModel Load()
    {
        var query = HttpContext.Request.Query["q"].FirstOrDefault() ?? "";
        List<AdminDatabaseRow> rows = [.. Store.Databases.Select(d => new AdminDatabaseRow(d.Id, d.Name, RegionsOf(d), d.Version, d.CreatedAt))];
        var filtered = AdminPresentation.FilterRows(
            rows, query, row => $"{row.DatabaseId} {row.Name} {row.Version} {string.Join(' ', row.Regions)}");
        return new AdminDatabasesPageModel(
            query,
            filtered,
            rows.SelectMany(r => r.Regions).Distinct().Count())
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

using TerraScale.Dashboard2;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard2.Features.Admin;

public sealed record AdminDatabaseRow(
    string DatabaseId,
    string Name,
    IReadOnlyList<string> Regions,
    string Version,
    string CreatedAt);

public sealed record AdminDatabasesPageModel(
    string Query,
    IReadOnlyList<AdminDatabaseRow> Rows,
    int RegionCount) : IPageModel
{
    public ShellModel Shell { get; init; } = null!;
}

public sealed class AdminDatabasesPageEndpoint(DashboardStore store)
    : SlicePageEndpoint<AdminDatabasesSlice, AdminDatabasesPageModel>(store, "/admin/databases")
{

    private static IReadOnlyList<string> RegionsOf(DashboardDatabase d)
    {
        var regions = new List<string> { d.PrimaryRegion };
        if (d.BackupRegion is { } backup)
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

    protected override AdminDatabasesPageModel Load() => new() { Shell = ShellFactory.Create(Store, PageRoute) };
}

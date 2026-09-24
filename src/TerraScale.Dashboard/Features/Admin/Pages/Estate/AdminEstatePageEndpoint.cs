using TerraScale.Dashboard;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Admin.Pages.Estate;

public sealed class AdminEstatePageEndpoint(DashboardStore store)
    : SlicePageEndpoint<AdminEstateSlice, AdminEstatePageModel>(store, "/admin/estate")
{
    protected override AdminEstatePageModel Load()
    {
        var tab = HttpContext.Request.Query["tab"].FirstOrDefault() switch
        {
            "hosts" => "hosts",
            "operations" => "operations",
            _ => "regions",
        };
        var query = HttpContext.Request.Query["q"].FirstOrDefault() ?? "";
        var notice = HttpContext.Request.Query["notice"].FirstOrDefault();
        var requestedPage = HttpContext.Request.Query["page"].FirstOrDefault();

        List<AdminRegionRow> regions = [.. Store.Regions.Select(r => new AdminRegionRow(r.Name, r.Code, CountryOf(r.Name), r.Status != "Offline", r.DatabaseCount))];

        List<AdminHostRow> allHosts = [.. Store.Databases.Select(d => new AdminHostRow(d.Id, d.Name, RegionsOf(d), d.Version, d.CreatedAt))];

        var hosts = AdminPresentation.FilterRows(
            allHosts, query, row => $"{row.DatabaseId} {row.Name} {string.Join(' ', row.Regions)}");
        var regionPage = AdminPresentation.Paginate(regions, tab == "regions" ? requestedPage : null);
        var hostPage = AdminPresentation.Paginate(hosts, tab == "hosts" ? requestedPage : null);

        var available = DashboardStore.EstateSections.Count(s => Store.AdminEstateFacts.TryGetValue(s.Key, out var facts) && !facts.IsDefaultOrEmpty && facts.Length > 0);

        return new AdminEstatePageModel(
            tab,
            query,
            notice,
            regionPage.Rows,
            hostPage.Rows,
            regions.Count(r => r.Available),
            regionPage.Number,
            regionPage.TotalPages,
            regionPage.TotalRows,
            hostPage.Number,
            hostPage.TotalPages,
            hostPage.TotalRows,
            Store.AdminEstateFacts,
            available,
            DashboardStore.EstateSections.Length)
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

    private static string CountryOf(string name) =>
        name.Contains(", ") ? name.Split(", ", 2)[1] : "";
}

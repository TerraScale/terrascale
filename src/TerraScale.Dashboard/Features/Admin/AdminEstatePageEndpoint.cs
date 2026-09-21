using TerraScale.Dashboard;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Admin;

public sealed class AdminEstatePageEndpoint(DashboardStore store)
    : SlicePageEndpoint<AdminEstateSlice, AdminEstatePageModel>(store, "/admin/estate")
{
    protected override AdminEstatePageModel Load()
    {
        var query = HttpContext.Request.Query["q"].FirstOrDefault() ?? "";
        var notice = HttpContext.Request.Query["notice"].FirstOrDefault();

        List<AdminRegionRow> regions = [.. Store.Regions.Select(r => new AdminRegionRow(r.Name, r.Code, CountryOf(r.Name), r.Status != "Offline", r.DatabaseCount))];

        List<AdminHostRow> allHosts = [.. Store.Databases.Select(d => new AdminHostRow(d.Id, d.Name, RegionsOf(d), d.Version, d.CreatedAt))];

        var hosts = AdminPresentation.FilterRows(
            allHosts, query, row => $"{row.DatabaseId} {row.Name} {string.Join(' ', row.Regions)}");

        var available = DashboardStore.EstateSections.Count(s => Store.AdminEstateFacts.TryGetValue(s.Key, out var facts) && !facts.IsDefaultOrEmpty && facts.Length > 0);

        return new AdminEstatePageModel(
            query,
            notice,
            regions,
            hosts,
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

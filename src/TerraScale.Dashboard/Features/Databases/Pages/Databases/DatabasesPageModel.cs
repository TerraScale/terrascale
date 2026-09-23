using System.Collections.Immutable;
using TerraScale.Dashboard;

namespace TerraScale.Dashboard.Features.Databases.Pages.Databases;

public sealed record DatabasesPageModel(
    ImmutableArray<DatabaseRow> Databases,
    IReadOnlyList<RegionMap.Pin> RegionMapPins,
    string Query,
    bool CreateOpen,
    string? Error) : IPageModel
{
    public ShellModel Shell { get; init; } = null!;

    public static DatabasesPageModel From(DashboardStore store, string? query, bool createOpen, string? error)
    {
        var project = store.ActiveProject;
        var rows = store.ProjectDatabases(project.Id).Select(store.ToRow);
        var term = (query ?? "").Trim().ToLowerInvariant();
        if (term.Length > 0)
        {
            rows = rows.Where(row => $"{row.Name} {row.Id} {row.Regions}".ToLowerInvariant().Contains(term));
        }

        // The create dialog reads placement, not input: it shows which of the
        // project's hubs the new database inherits.
        var hub = DashboardStore.HubCode(project.Region);
        return new(
            Databases: [.. rows],
            RegionMapPins: [.. store.OrganizationRegions.Select(region =>
                region.ToMapPin(string.Equals(region.Code, hub, StringComparison.OrdinalIgnoreCase)))],
            Query: query ?? "",
            CreateOpen: createOpen,
            Error: error)
        {
            Shell = ShellFactory.Create(store, "/databases"),
        };
    }

    public static string RegionDisplay(string code) => code.ToUpperInvariant() + " · " + RegionName(code);

    private static string RegionName(string code) => code.ToLowerInvariant() switch
    {
        "sfo" => "San Francisco",
        "fra" => "Frankfurt",
        "gru" => "Sao Paulo",
        _ => code.ToUpperInvariant(),
    };
}

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
    public const int PageSize = 10;

    public ShellModel Shell { get; init; } = null!;

    public int Page { get; init; } = 1;
    public int PageCount { get; init; } = 1;
    public int Total { get; init; }

    public int First => Total is 0 ? 0 : ((Page - 1) * PageSize) + 1;

    public int Last => Math.Min(Page * PageSize, Total);

    public static DatabasesPageModel From(DashboardStore store, string? query, bool createOpen, string? error, int page = 1)
    {
        var project = store.ActiveProject;
        var rows = store.ProjectDatabases(project.Id).Select(store.ToRow);
        var term = (query ?? "").Trim().ToLowerInvariant();
        if (term.Length > 0)
        {
            rows = rows.Where(row => $"{row.Name} {row.Id} {row.Regions}".ToLowerInvariant().Contains(term));
        }

        var filtered = rows.ToList();
        var pageCount = Math.Max(1, (filtered.Count + PageSize - 1) / PageSize);
        var currentPage = Math.Clamp(page, 1, pageCount);
        var visible = filtered.Skip((currentPage - 1) * PageSize).Take(PageSize);

        // The create dialog reads placement, not input: it shows which of the
        // project's hubs the new database inherits.
        var hub = DashboardStore.HubCode(project.Region);
        return new(
            Databases: [.. visible],
            RegionMapPins: [.. store.OrganizationRegions.Select(region =>
                region.ToMapPin(string.Equals(region.Code, hub, StringComparison.OrdinalIgnoreCase)))],
            Query: query ?? "",
            CreateOpen: createOpen,
            Error: error)
        {
            Shell = ShellFactory.Create(store, "/databases"),
            Page = currentPage,
            PageCount = pageCount,
            Total = filtered.Count,
        };
    }

    public string Href(int page)
    {
        var query = new List<string>();
        if (Query.Length > 0)
        {
            query.Add($"q={Uri.EscapeDataString(Query)}");
        }

        if (page > 1)
        {
            query.Add($"page={page}");
        }

        return query.Count is 0 ? "/databases" : "/databases?" + string.Join('&', query);
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

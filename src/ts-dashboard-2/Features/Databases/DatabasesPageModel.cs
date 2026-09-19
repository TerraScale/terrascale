using System.Collections.Immutable;
using TerraScale.Dashboard2;

namespace TerraScale.Dashboard2.Features.Databases;

public sealed record DatabasesPageModel(
    ImmutableArray<DatabaseRow> Databases,
    ImmutableArray<string> ProjectRegions,
    string Query,
    bool CreateOpen,
    string? Error) : IPageModel
{
    public ShellModel Shell { get; init; } = null!;

    /// <summary>The v1 region catalog: exactly the three airport hubs.</summary>
    public static readonly ImmutableArray<RegionHub> RegionHubs =
    [
        new("sfo", "San Francisco", 21, 28.58),
        new("fra", "Frankfurt", 71.5, 20.78),
        new("gru", "Sao Paulo", 50, 59.76),
    ];

    public static DatabasesPageModel From(DashboardStore store, string? query, bool createOpen, string? error)
    {
        var project = store.ActiveProject;
        var rows = store.ProjectDatabases(project.Id).Select(store.ToRow);
        var term = (query ?? "").Trim().ToLowerInvariant();
        if (term.Length > 0)
        {
            rows = rows.Where(row => $"{row.Name} {row.Id} {row.Regions}".ToLowerInvariant().Contains(term));
        }

        return new(
            Databases: [.. rows],
            ProjectRegions: [DashboardStore.HubCode(project.Region)],
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

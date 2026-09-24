using System.Collections.Immutable;
using TerraScale.Dashboard;

namespace TerraScale.Dashboard.Features.Data.Pages.Indexes;

public sealed record IndexesPageModel(
    string Collection,
    ImmutableArray<IndexState> Indexes,
    ImmutableArray<QueryTemplate> Templates,
    ImmutableArray<IndexRecommendation> Recommendations,
    IndexMigration Migration,
    string? ActionError) : IPageModel
{
    public const int PageSize = 10;

    public ShellModel Shell { get; init; } = null!;
    public string Tab { get; init; } = "overview";
    public int Page { get; init; } = 1;
    public int PageCount { get; init; } = 1;
    public int Total { get; init; }

    public int First => Total is 0 ? 0 : ((Page - 1) * PageSize) + 1;
    public int Last => Math.Min(Page * PageSize, Total);
    public IReadOnlyList<IndexState> VisibleIndexes { get; init; } = [];
    public IReadOnlyList<QueryTemplate> VisibleTemplates { get; init; } = [];
    public IReadOnlyList<IndexRecommendation> VisibleRecommendations { get; init; } = [];

    public string Href(int page)
    {
        var query = new List<string>
        {
            $"tab={Uri.EscapeDataString(Tab)}",
        };
        if (Collection.Length > 0)
        {
            query.Add($"collection={Uri.EscapeDataString(Collection)}");
        }

        if (page > 1)
        {
            query.Add($"page={page}");
        }

        return "/indexes?" + string.Join('&', query);
    }

    public static IndexesPageModel From(DashboardStore store, string? collection, string? actionError,
        string? tab, int page = 1)
    {
        var indexTab = tab switch
        {
            "templates" => "templates",
            "recommendations" => "recommendations",
            "migration" => "migration",
            _ => actionError is not null ? "recommendations" : "overview",
        };
        var indexes = store.Indexes;
        var templates = store.QueryTemplates;
        var recommendations = store.IndexRecommendations;
        var total = indexTab switch
        {
            "templates" => templates.Length,
            "recommendations" => recommendations.Length,
            "overview" => indexes.Length,
            _ => 0,
        };
        var pageCount = Math.Max(1, (total + PageSize - 1) / PageSize);
        var currentPage = Math.Clamp(page, 1, pageCount);
        var offset = (currentPage - 1) * PageSize;

        return new(
            NormalizeCollection(collection),
            indexes,
            templates,
            recommendations,
            store.IndexMigrationState,
            actionError)
        {
            Tab = indexTab,
            Page = currentPage,
            PageCount = pageCount,
            Total = total,
            VisibleIndexes = indexes.Skip(offset).Take(PageSize).ToArray(),
            VisibleTemplates = templates.Skip(offset).Take(PageSize).ToArray(),
            VisibleRecommendations = recommendations.Skip(offset).Take(PageSize).ToArray(),
            Shell = ShellFactory.Create(store, "/indexes"),
        };
    }

    /// <summary>schema-data.ts normalizeCollection: trimmed non-empty up to 128 chars, else "default".</summary>
    private static string NormalizeCollection(string? value)
    {
        var collection = value?.Trim() ?? "";
        return collection.Length > 0 && collection.Length <= 128 ? collection : "default";
    }
}

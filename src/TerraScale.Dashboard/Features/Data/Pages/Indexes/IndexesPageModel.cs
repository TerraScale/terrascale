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
    public ShellModel Shell { get; init; } = null!;

    public static IndexesPageModel From(DashboardStore store, string? collection, string? actionError) => new(
        NormalizeCollection(collection),
        store.Indexes,
        store.QueryTemplates,
        store.IndexRecommendations,
        store.IndexMigrationState,
        actionError)
    {
        Shell = ShellFactory.Create(store, "/indexes"),
    };

    /// <summary>schema-data.ts normalizeCollection: trimmed non-empty up to 128 chars, else "default".</summary>
    private static string NormalizeCollection(string? value)
    {
        var collection = value?.Trim() ?? "";
        return collection.Length > 0 && collection.Length <= 128 ? collection : "default";
    }
}

using System.Collections.Immutable;
using TerraScale.Dashboard2;

namespace TerraScale.Dashboard2.Features.Data;

public sealed record IndexesPageModel(
    ImmutableArray<IndexState> Indexes,
    ImmutableArray<QueryTemplate> Templates,
    ImmutableArray<IndexRecommendation> Recommendations,
    IndexMigration Migration) : IPageModel
{
    public ShellModel Shell { get; init; } = null!;

    public static IndexesPageModel From(DashboardStore store) => new(
        store.Indexes,
        store.QueryTemplates,
        store.IndexRecommendations,
        store.IndexMigrationState)
    {
        Shell = ShellFactory.Create(store, "/indexes"),
    };
}

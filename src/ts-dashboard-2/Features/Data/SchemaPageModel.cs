using TerraScale.Dashboard2;

namespace TerraScale.Dashboard2.Features.Data;

public sealed record SchemaPageModel(
    SchemaDocument Document,
    string ProposedJson,
    SchemaDiff? Diff,
    bool ShowDiff,
    string? Error) : IPageModel
{
    public ShellModel Shell { get; init; } = null!;

    public static SchemaPageModel From(DashboardStore store, bool showDiff, string? error) => new(
        Document: store.Schema,
        ProposedJson: store.ProposedSchemaJson,
        Diff: showDiff ? store.PendingSchemaDiff : null,
        ShowDiff: showDiff,
        Error: error)
    {
        Shell = ShellFactory.Create(store, "/schema"),
    };
}

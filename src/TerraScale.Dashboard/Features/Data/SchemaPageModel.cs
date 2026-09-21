using TerraScale.Dashboard;

namespace TerraScale.Dashboard.Features.Data;

public sealed record SchemaPageModel(
    SchemaDocument Document,
    string Collection,
    string ProposedJson,
    SchemaDiff? Diff,
    bool Applied,
    string? LoadError,
    string? DraftError) : IPageModel
{
    public ShellModel Shell { get; init; } = null!;

    public static SchemaPageModel From(DashboardStore store, string? collection, bool showDiff, bool applied,
        string? loadError, string? draftError) => new(
        Document: store.Schema,
        Collection: NormalizeCollection(collection),
        ProposedJson: store.ProposedSchemaJson,
        Diff: showDiff || applied ? store.PendingSchemaDiff : null,
        Applied: applied,
        LoadError: loadError,
        DraftError: draftError)
    {
        Shell = ShellFactory.Create(store, "/schema"),
    };

    /// <summary>schema-data.ts normalizeCollection: trimmed non-empty up to 128 chars, else "default".</summary>
    private static string NormalizeCollection(string? value)
    {
        var collection = value?.Trim() ?? "";
        return collection.Length > 0 && collection.Length <= 128 ? collection : "default";
    }
}

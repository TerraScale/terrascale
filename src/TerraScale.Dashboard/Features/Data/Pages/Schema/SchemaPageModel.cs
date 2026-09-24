using TerraScale.Dashboard;

namespace TerraScale.Dashboard.Features.Data.Pages.Schema;

public sealed record SchemaPageModel(
    SchemaDocument Document,
    string Collection,
    string ProposedJson,
    SchemaDiff? Diff,
    bool Applied,
    string? LoadError,
    string? DraftError) : IPageModel
{
    public const int PageSize = 10;

    public ShellModel Shell { get; init; } = null!;
    public int Page { get; init; } = 1;
    public int PageCount { get; init; } = 1;

    public int Total => Document.Collections.Length;
    public int First => Total is 0 ? 0 : ((Page - 1) * PageSize) + 1;
    public int Last => Math.Min(Page * PageSize, Total);
    public IReadOnlyList<SchemaCollection> VisibleCollections =>
        Document.Collections.Skip((Page - 1) * PageSize).Take(PageSize).ToArray();

    public string Href(int page)
    {
        var query = new List<string> { "tab=viewer" };
        if (Collection.Length > 0)
        {
            query.Add($"collection={Uri.EscapeDataString(Collection)}");
        }

        if (page > 1)
        {
            query.Add($"page={page}");
        }

        return "/schema?" + string.Join('&', query);
    }

    public static SchemaPageModel From(DashboardStore store, string? collection, bool showDiff, bool applied,
        string? loadError, string? draftError, int page = 1) => new(
        Document: store.Schema,
        Collection: NormalizeCollection(collection),
        ProposedJson: store.ProposedSchemaJson,
        Diff: showDiff || applied ? store.PendingSchemaDiff : null,
        Applied: applied,
        LoadError: loadError,
        DraftError: draftError)
    {
        Page = Math.Clamp(page, 1, Math.Max(1, (store.Schema.Collections.Length + PageSize - 1) / PageSize)),
        PageCount = Math.Max(1, (store.Schema.Collections.Length + PageSize - 1) / PageSize),
        Shell = ShellFactory.Create(store, "/schema"),
    };

    /// <summary>schema-data.ts normalizeCollection: trimmed non-empty up to 128 chars, else "default".</summary>
    private static string NormalizeCollection(string? value)
    {
        var collection = value?.Trim() ?? "";
        return collection.Length > 0 && collection.Length <= 128 ? collection : "default";
    }
}

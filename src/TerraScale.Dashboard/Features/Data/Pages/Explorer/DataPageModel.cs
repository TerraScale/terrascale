using System.Collections.Immutable;
using TerraScale.Dashboard;

namespace TerraScale.Dashboard.Features.Data.Pages.Explorer;

public sealed record DataPageModel(
    string View,                       // queries | table | editor | sql
    (string DatabaseId, string Name)[] Databases,
    string SelectedDatabase,
    string SelectedCollection,
    ImmutableArray<ExplorerDocument> Documents,
    string? Error,
    string? Notice,
    SavedQuery? Editing,               // dialog state: ?edit=<id>
    string? DeletingId,                // dialog state: ?delete=<id>
    bool Conflict,
    string Sql,
    string SqlCollection,
    bool SqlChecked,
    SqlAnalysis? SqlAnalysis) : IPageModel
{
    public ShellModel Shell { get; init; } = null!;

    public static DataPageModel From(DashboardStore store, string view, string? database, string? collection,
        string? edit, string? delete, bool conflict, string? error, string? notice,
        string? sql, string? sqlCollection, bool sqlChecked)
    {
        var selectedDatabase = database ?? "";
        var selectedCollection = collection ?? "";
        // v1 analyses the statement on every render, so the default statement shows its verdict on first load.
        var effectiveSql = sql is { Length: > 0 } ? sql : "SELECT * FROM orders LIMIT 25";
        var analysis = view is "sql" ? SqlAnalyzer.Analyze(effectiveSql) : null;
        return new(
            View: view,
            Databases: [.. store.ProjectDatabases(store.ActiveProjectId).Select(d => (d.Id, d.Name))],
            SelectedDatabase: selectedDatabase,
            SelectedCollection: selectedCollection,
            Documents: selectedDatabase.Length > 0 && selectedCollection.Length > 0
                ? store.ExplorerDocuments(selectedDatabase, selectedCollection)
                : [],
            Error: error,
            Notice: notice,
            Editing: edit is { Length: > 0 } id ? store.SavedQueries.FirstOrDefault(q => q.Id == id) : null,
            DeletingId: delete,
            Conflict: conflict,
            Sql: effectiveSql,
            SqlCollection: sqlCollection is { Length: > 0 } c ? c : "orders",
            SqlChecked: sqlChecked,
            SqlAnalysis: analysis)
        {
            Shell = ShellFactory.Create(store, "/data"),
        };
    }
}

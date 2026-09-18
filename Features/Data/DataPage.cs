using System.Collections.Immutable;
using TerraScale.Dashboard2;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard2.Features.Data;

public sealed record DataPageModel(
    string View,                       // queries | table | editor | sql
    string ProjectName,
    string LoadedAt,
    int SavedQueryCount,
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

    /// <summary>Intl "medium date, short time" UTC rendering of the loaded-at stamp.</summary>
    public string LoadedAtText => DateTimeOffset.Parse(LoadedAt, System.Globalization.CultureInfo.InvariantCulture)
        .ToString("MMM d, yyyy, h:mm tt", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>shortId from src/lib/formatting.ts.</summary>
    public string ShortProject => ProjectName.Length <= 12 ? ProjectName : $"{ProjectName[..6]}...{ProjectName[^4..]}";

    public static DataPageModel From(DashboardStore store, string view, string? database, string? collection,
        string? edit, string? delete, bool conflict, string? error, string? notice,
        string? sql, string? sqlCollection, bool sqlChecked)
    {
        var selectedDatabase = database ?? "";
        var selectedCollection = collection ?? "";
        var analysis = view is "sql" && (sql ?? "").Length > 0 ? SqlAnalyzer.Analyze(sql!) : null;
        return new(
            View: view,
            ProjectName: store.ActiveProject.Name,
            LoadedAt: DateTimeOffset.Now.ToString("O"),
            SavedQueryCount: store.SavedQueries.Length,
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
            Sql: sql ?? "SELECT * FROM orders LIMIT 25",
            SqlCollection: sqlCollection is { Length: > 0 } c ? c : "orders",
            SqlChecked: sqlChecked,
            SqlAnalysis: analysis)
        {
            Shell = ShellFactory.Create(store, "/data"),
        };
    }
}

public sealed class DataPageEndpoint(DashboardStore store) : SlicePageEndpoint<DataSlice, DataPageModel>(store, "/data")
{
    protected override DataPageModel Load()
    {
        var query = HttpContext.Request.Query;
        // The saved-query picker GETs ?savedQueryId=; resolve it to its SQL text.
        var sql = query["sql"].ToString();
        if (sql.Length == 0 && query["savedQueryId"].ToString() is { Length: > 0 } id)
        {
            sql = Store.SavedQueries.FirstOrDefault(q => q.Id == id)?.Query ?? "";
        }

        return DataPageModel.From(
            Store,
            view: query["view"].ToString() is { Length: > 0 } v ? v : "queries",
            database: query["database"].ToString(),
            collection: query["collection"].ToString(),
            edit: query["edit"].ToString(),
            delete: query["delete"].ToString(),
            conflict: query["conflict"].ToString() == "1",
            error: query["error"].ToString() is { Length: > 0 } e ? e : null,
            notice: query["notice"].ToString() is { Length: > 0 } n ? n : null,
            sql: sql.Length > 0 ? sql : null,
            sqlCollection: query["collection"].ToString(),
            sqlChecked: query["checked"].ToString() == "1");
    }
}

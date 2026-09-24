using System.Collections.Immutable;
using TerraScale.Dashboard;

namespace TerraScale.Dashboard.Features.Data.Pages.Explorer;

public sealed record DataPageModel(
    string View,                       // queries | table | editor | sql
    (string DatabaseId, string Name)[] Databases,
    string SelectedDatabase,
    string SelectedCollection,
    ImmutableArray<ExplorerDocument> Documents,
    ImmutableArray<SavedQuery> SavedQueries,
    string? Error,
    string? Notice,
    SavedQuery? Editing,               // dialog state: ?edit=<id>
    string? DeletingId,                // dialog state: ?delete=<id>
    bool Conflict,
    string Sql,
    string SqlCollection,
    bool SqlChecked,
    SqlAnalysis? SqlAnalysis,
    string Filter,
    int Page) : IPageModel
{
    public const int PageSize = 10;

    public ShellModel Shell { get; init; } = null!;

    public ImmutableArray<SavedQuery> VisibleSavedQueries => [..
        SavedQueries
            .Where(query => Filter.Length == 0
                || query.Name.Contains(Filter, StringComparison.OrdinalIgnoreCase)
                || query.Query.Contains(Filter, StringComparison.OrdinalIgnoreCase))
            .Skip((Page - 1) * PageSize)
            .Take(PageSize)];

    public ImmutableArray<ExplorerDocument> VisibleDocuments => [..
        Documents
            .Where(document => Filter.Length == 0 || document.Json.Contains(Filter, StringComparison.OrdinalIgnoreCase))
            .Skip((Page - 1) * PageSize)
            .Take(PageSize)];

    public int ResultTotal => View == "queries"
        ? SavedQueries.Count(query => Filter.Length == 0
            || query.Name.Contains(Filter, StringComparison.OrdinalIgnoreCase)
            || query.Query.Contains(Filter, StringComparison.OrdinalIgnoreCase))
        : Documents.Count(document => Filter.Length == 0 || document.Json.Contains(Filter, StringComparison.OrdinalIgnoreCase));

    public int ResultPageCount => Math.Max(1, (ResultTotal + PageSize - 1) / PageSize);

    public int ResultFirst => ResultTotal is 0 ? 0 : ((Page - 1) * PageSize) + 1;

    public int ResultLast => Math.Min(Page * PageSize, ResultTotal);

    public string ResultHref(int page)
    {
        var href = $"/data?view={Uri.EscapeDataString(View)}";
        if (View == "table")
        {
            href += $"&database={Uri.EscapeDataString(SelectedDatabase)}&collection={Uri.EscapeDataString(SelectedCollection)}";
        }

        if (Filter.Length > 0)
        {
            href += $"&q={Uri.EscapeDataString(Filter)}";
        }

        if (page > 1)
        {
            href += $"&page={page}";
        }

        return href;
    }

    public static DataPageModel From(DashboardStore store, string view, string? database, string? collection,
        string? edit, string? delete, bool conflict, string? error, string? notice,
        string? sql, string? sqlCollection, bool sqlChecked, string? filter, int page)
    {
        var selectedDatabase = database ?? "";
        var selectedCollection = collection ?? "";
        var normalizedView = view is "queries" or "table" or "editor" or "sql" ? view : "queries";
        var normalizedFilter = filter?.Trim() ?? "";
        // v1 analyses the statement on every render, so the default statement shows its verdict on first load.
        var effectiveSql = sql is { Length: > 0 } ? sql : "SELECT * FROM orders LIMIT 25";
        var analysis = normalizedView is "sql" ? SqlAnalyzer.Analyze(effectiveSql) : null;
        var documents = selectedDatabase.Length > 0 && selectedCollection.Length > 0
            ? store.ExplorerDocuments(selectedDatabase, selectedCollection)
            : ImmutableArray<ExplorerDocument>.Empty;
        var filteredCount = normalizedView == "queries"
            ? store.SavedQueries.Count(query => normalizedFilter.Length == 0
                || query.Name.Contains(normalizedFilter, StringComparison.OrdinalIgnoreCase)
                || query.Query.Contains(normalizedFilter, StringComparison.OrdinalIgnoreCase))
            : documents.Count(document => normalizedFilter.Length == 0
                || document.Json.Contains(normalizedFilter, StringComparison.OrdinalIgnoreCase));
        var pageCount = Math.Max(1, (filteredCount + PageSize - 1) / PageSize);
        var normalizedPage = Math.Clamp(page, 1, pageCount);
        return new(
            View: normalizedView,
            Databases: [.. store.ProjectDatabases(store.ActiveProjectId).Select(d => (d.Id, d.Name))],
            SelectedDatabase: selectedDatabase,
            SelectedCollection: selectedCollection,
            Documents: documents,
            SavedQueries: store.SavedQueries,
            Error: error,
            Notice: notice,
            Editing: edit is { Length: > 0 } id ? store.SavedQueries.FirstOrDefault(q => q.Id == id) : null,
            DeletingId: delete,
            Conflict: conflict,
            Sql: effectiveSql,
            SqlCollection: sqlCollection is { Length: > 0 } c ? c : "orders",
            SqlChecked: sqlChecked,
            SqlAnalysis: analysis,
            Filter: normalizedFilter,
            Page: normalizedPage)
        {
            Shell = ShellFactory.Create(store, "/data"),
        };
    }
}

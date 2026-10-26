using System.Text.Json;
using TerraScale.Dashboard.Features.Accounts;
using TerraScale.Dashboard.Features.Databases;
using TerraScale.Dashboard.Shared.UI;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.Dashboard.Features.Data;

public sealed record DataPage(ShellModel Shell, DatabaseRecord Database, string Section, string Collection,
    IReadOnlyList<TerraBaseCollection> Collections, DocumentBrowse Browse, string Search = "", string Sort = "", bool Descending = false,
    string Layout = "grid", string Error = "", string FormError = "", string Dialog = "", string DocumentId = "", string DocumentJson = "{}",
    string Version = "", string Operation = "replace", string Sql = "", DatabaseSqlResult? SqlResult = null,
    IReadOnlyList<DatabaseIndex>? Indexes = null, string Schema = "", IReadOnlyList<SavedQuery>? SavedQueries = null, string QueryName = "",
    SqlCatalog? Catalog = null, IReadOnlyList<string>? History = null, IReadOnlyDictionary<string, long>? CollectionCounts = null,
    bool SqlChecked = false, string CatalogError = "")
{
    public AccountContext Context => Shell.Context;
    public SqlAnalysis Analysis => SqlValidation.Analyze(Sql);
    public string CollectionLabel(string name) => CollectionCounts is not null && CollectionCounts.TryGetValue(name, out var count)
        ? name + " (" + TerraScale.Dashboard.Shared.Charts.ChartGeometry.Compact(count) + ")" : name;
    public string? CollectionTitle(string name) => CollectionCounts is not null && CollectionCounts.TryGetValue(name, out var count)
        ? count.ToString("N0", System.Globalization.CultureInfo.InvariantCulture) + " documents" : null;
    public DataGrid Grid => DataGrid.Create(Browse.Documents.Select(document => new DatabaseSqlRow(document.Id, document.Document)).ToArray(), Browse.Matched, Browse.More);
    public string BrowsePath(params (string Key, string Value)[] changes)
    {
        var query = new Dictionary<string, string>();
        if (Layout == "list") query["view"] = "list";
        if (Search.Length > 0) query["q"] = Search;
        if (Sort.Length > 0) query["sort"] = Sort;
        if (Descending) query["order"] = "desc";
        if (Browse.Page > 1) query["page"] = Browse.Page.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (changes.Any(change => change.Key is "q" or "sort" or "order")) { query.Remove("cursor"); query.Remove("page"); }
        foreach (var change in changes) { if (change.Value.Length == 0) query.Remove(change.Key); else query[change.Key] = change.Value; }
        var path = "/databases/" + Uri.EscapeDataString(Database.Id) + "/documents/" + Uri.EscapeDataString(Collection);
        return path + (query.Count == 0 ? "" : "?" + string.Join('&', query.Select(pair => Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value))));
    }
    public string SortPath(string field) => BrowsePath(("sort", field), ("order", Sort == field && !Descending ? "desc" : ""), ("page", ""));
    public string NextPath => BrowsePath(("cursor", ""), ("page", (Browse.Page + 1).ToString(System.Globalization.CultureInfo.InvariantCulture)));
    public string FirstPath => BrowsePath(("cursor", ""), ("page", ""));
    public string Summary => Error.Length > 0 ? "Document results unavailable." : Search.Length > 0
        ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{Browse.Documents.Count:N0} matches in {Browse.Scanned:N0} documents checked this scan · ") + (Browse.More ? "Partial search. Continue searching the collection." : "End of collection reached.")
        : string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{Browse.Documents.Count:N0} documents on page {Browse.Page:N0} · ") + (Browse.More ? "More documents available." : "End of collection reached.");
    public string EmptyTitle => Error.Length > 0 ? "Documents could not be loaded." : Search.Length > 0
        ? Browse.More ? "No matches in this scan." : Browse.Cursor.Length > 0 ? "No more matches." : "No documents match your search."
        : Browse.Cursor.Length > 0 ? "No more documents." : "This collection has no documents yet.";
    public string EmptyDetail => Error.Length > 0 ? "Reload this page to try again." : Search.Length > 0
        ? Browse.More ? "Continue searching to check the next documents in this collection." : "Change or clear your search to browse documents."
        : "Add a document or open another collection.";
}

public sealed record DataGridColumn(string Name, bool Numeric, bool Root)
{
    public bool Sortable => !Root && Name.Length is > 0 and <= 128 && !Name.Contains('.', StringComparison.Ordinal);
    public string Label => !Root && Name.Equals("id", StringComparison.OrdinalIgnoreCase) ? "payload." + Name : Name;
}
public sealed record QueryDeleteModel(DataPage Page, SavedQuery Query);
public sealed record DataGridCell(string Text, string Kind, string? Title = null);
public sealed record DataGridRow(string Id, IReadOnlyList<DataGridCell> Cells);
public sealed record DataGrid(IReadOnlyList<DataGridColumn> Columns, IReadOnlyList<DataGridRow> Rows, int Matched, bool More, int HiddenColumns, string Json)
{
    public static DataGrid Create(IReadOnlyList<DatabaseSqlRow> input, int matched, bool more)
    {
        var rows = input.Take(200).ToArray();
        var names = new List<(string Name, bool Root)>();
        var values = new List<Dictionary<(string Name, bool Root), JsonElement>>();
        foreach (var row in rows)
        {
            var fields = new Dictionary<(string, bool), JsonElement>();
            if (row.Value.ValueKind == JsonValueKind.Object)
            {
                foreach (var field in row.Value.EnumerateObject())
                {
                    if (field.Name == "id" && field.Value.ValueKind == JsonValueKind.String && field.Value.GetString() == row.Id) continue;
                    fields[(field.Name, false)] = field.Value;
                    if (!names.Contains((field.Name, false))) names.Add((field.Name, false));
                }
            }
            else { fields[("value", true)] = row.Value; if (!names.Contains(("value", true))) names.Add(("value", true)); }
            values.Add(fields);
        }
        var hidden = Math.Max(0, names.Count - 24);
        names = names.Take(24).ToList();
        var columns = names.Select(name => new DataGridColumn(name.Name, values.All(fields => !fields.TryGetValue(name, out var value) || value.ValueKind == JsonValueKind.Number), name.Root)).ToArray();
        var laidOut = rows.Select((row, index) => new DataGridRow(row.Id, names.Select(name => values[index].TryGetValue(name, out var value) ? Cell(value) : new DataGridCell("", "missing")).ToArray())).ToArray();
        return new(columns, laidOut, Math.Max(input.Count, matched), more, hidden, JsonSerializer.Serialize(rows, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
    }

    private static DataGridCell Cell(JsonElement value)
    {
        var kind = value.ValueKind switch { JsonValueKind.String => "string", JsonValueKind.Number => "number", JsonValueKind.True or JsonValueKind.False => "bool", JsonValueKind.Null => "null", JsonValueKind.Object => "object", JsonValueKind.Array => "array", _ => "missing" };
        var text = value.ValueKind == JsonValueKind.String ? value.GetString()! : JsonSerializer.Serialize(value);
        var runes = text.EnumerateRunes().Select(rune => rune.ToString()).ToArray();
        var title = kind != "number" && runes.Length > 160 ? string.Concat(runes.Take(639)).TrimEnd() + (runes.Length > 640 ? "…" : "") : null;
        if (kind != "number" && runes.Length > 160) text = string.Concat(runes.Take(159)).TrimEnd() + "…";
        return new(text, kind, title);
    }
}

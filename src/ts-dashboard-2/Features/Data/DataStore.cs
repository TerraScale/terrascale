using System.Collections.Immutable;
using System.Text.Json;
using TerraScale.Dashboard2.Features.Data;

namespace TerraScale.Dashboard2;

// Store extension: mutations + read-only fixtures for the Data Explorer pages.
// Per-account state lives on the active world.
public sealed partial class DashboardStore
{
    public ImmutableArray<ExplorerDocument> ExplorerDocumentsState { get => World.ExplorerDocuments; private set => World.ExplorerDocuments = value; }

    public SchemaDocument Schema { get => World.Schema; private set => World.Schema = value; }

    public SchemaDiff? PendingSchemaDiff { get => World.PendingSchemaDiff; private set => World.PendingSchemaDiff = value; }

    public string ProposedSchemaJson { get => World.ProposedSchemaJson; private set => World.ProposedSchemaJson = value; }

    public ImmutableArray<IndexState> Indexes { get => World.Indexes; private set => World.Indexes = value; }

    public ImmutableArray<QueryTemplate> QueryTemplates { get => World.QueryTemplates; private set => World.QueryTemplates = value; }

    public ImmutableArray<IndexRecommendation> IndexRecommendations { get => World.IndexRecommendations; private set => World.IndexRecommendations = value; }

    public IndexMigration IndexMigrationState { get; private set; } = new("running", 8_640, 12_480, "doc_01JC9XQ1T3V5X7Z9B2D4F7HJ2");

    public DocumentHistoryState DocumentHistory { get; private set; } = new(
        Message: "A database, collection, and document are required to view history.",
        RetentionDays: 30,
        LimitedByRetention: false);

    // --- Queries -----------------------------------------------------------

    public ImmutableArray<ExplorerDocument> ExplorerDocuments(string database, string collection) =>
        database is "core-primary" && collection is "orders" ? ExplorerDocumentsState : [];

    // --- Mutations ---------------------------------------------------------

    public bool UpdateSavedQuery(string id, string name, string query, string revision)
    {
        var current = SavedQueries.FirstOrDefault(q => q.Id == id);
        if (current is null || current.Revision != revision)
        {
            return false;
        }

        SavedQueries = SavedQueries.Select(q => q.Id == id
            ? q with { Name = name, Query = query, Revision = (int.TryParse(revision, out var number) ? number + 1 : number + 1).ToString(), UpdatedAt = DateTimeOffset.Now.ToString("O") }
            : q).ToImmutableArray();
        return true;
    }

    public string? DeleteSavedQueryAndReturnName(string id)
    {
        var target = SavedQueries.FirstOrDefault(q => q.Id == id);
        if (target is null)
        {
            return null;
        }

        SavedQueries = SavedQueries.Where(q => q.Id != id).ToImmutableArray();
        return target.Name;
    }

    public string? MutateExplorerDocument(string operation, string id, string json)
    {
        if (operation is "delete")
        {
            ExplorerDocumentsState = ExplorerDocumentsState.Remove(ExplorerDocumentsState.FirstOrDefault(d => d.Id == id) ?? new("", ""));
            return null;
        }

        try
        {
            using var _ = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return "Document is not valid JSON.";
        }

        var documentId = id.Length > 0 ? id : $"doc_{Guid.NewGuid():N}"[..24];
        var normalized = json.Trim();
        ExplorerDocumentsState = operation switch
        {
            "create" => ExplorerDocumentsState.Add(new(documentId, normalized)),
            _ => ExplorerDocumentsState.Select(d => d.Id == documentId ? new ExplorerDocument(documentId, normalized) : d).ToImmutableArray(),
        };
        return null;
    }

    public bool ApplyIndexRecommendation(string id)
    {
        var recommendation = IndexRecommendations.FirstOrDefault(r => r.Id == id);
        if (recommendation is null || recommendation.State != "recommended" || recommendation.Fields.Length == 0)
        {
            return false;
        }

        IndexRecommendations = IndexRecommendations.Select(r => r.Id == id ? r with { State = "applied" } : r).ToImmutableArray();
        Indexes = Indexes.Add(new(
            $"idx_{Guid.NewGuid():N}"[..24],
            string.Join("_", recommendation.Fields) + "_idx",
            [.. recommendation.Fields],
            "building",
            null,
            recommendation.ExpectedCostReduction.GetValueOrDefault() * 40,
            null,
            null));
        return true;
    }

    public string? PutSchema(string proposedJson)
    {
        proposedJson = proposedJson.Trim();
        if (proposedJson.Length == 0)
        {
            PendingSchemaDiff = null;
            ProposedSchemaJson = "";
            return "Enter a valid schema JSON document.";
        }

        JsonDocument parsed;
        try
        {
            parsed = JsonDocument.Parse(proposedJson);
        }
        catch (JsonException)
        {
            return "Schema JSON is not valid JSON.";
        }

        if (!parsed.RootElement.TryGetProperty("collections", out var collections) || collections.ValueKind != JsonValueKind.Array)
        {
            return "Schema JSON must contain a collections array.";
        }

        var proposed = collections.EnumerateArray()
            .Select(ParseSchemaCollection)
            .Where(c => c.Name.Length > 0)
            .ToArray();

        // Port of schema-data.ts diffSchemas/diffNamedItems.
        var changes = new List<SchemaDiffChange>();
        var before = Schema.Collections.ToDictionary(c => c.Name);
        var after = proposed.ToDictionary(c => c.Name);
        foreach (var (name, collection) in after)
        {
            if (!before.TryGetValue(name, out var previous))
            {
                changes.Add(new("added", $"collections.{name}"));
                continue;
            }

            DiffNamedItems(changes, $"collections.{name}.fields",
                previous.Fields.Select(f => (f.Name, Key: FieldKey(f))),
                collection.Fields.Select(f => (f.Name, Key: FieldKey(f))));
            DiffNamedItems(changes, $"collections.{name}.indexes",
                previous.Indexes.Select(i => (i.Name, Key: IndexKey(i))),
                collection.Indexes.Select(i => (i.Name, Key: IndexKey(i))));
            if ((previous.HistoryRetentionDays?.ToString() ?? "") != (collection.HistoryRetentionDays?.ToString() ?? ""))
            {
                changes.Add(new("changed", $"collections.{name}.historyRetentionDays"));
            }
        }

        foreach (var name in before.Keys.Where(name => !after.ContainsKey(name)))
        {
            changes.Add(new("removed", $"collections.{name}"));
        }

        // The apply succeeds: adopt the proposed schema and start its migration (v1 putSchema response).
        PendingSchemaDiff = new SchemaDiff([.. changes], changes.Any(c => c.Kind == "removed" || c.Path.EndsWith(".fields")));
        ProposedSchemaJson = JsonSerializer.Serialize(parsed, Indented);
        Schema = new SchemaDocument(
            [.. proposed],
            Version: Schema.Version + 1,
            UpdatedAt: DateTimeOffset.UtcNow.ToString("O"),
            Migration: new("running", 0, 12_480, $"wal-{Guid.NewGuid().ToString("N")[..10].ToUpperInvariant()}"));
        return null;
    }

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    private static string FieldKey(SchemaField field) => $"{field.Name}|{field.Type}|{field.Required}|{field.Indexed}";

    private static string IndexKey(SchemaIndex index) => $"{index.Name}|{string.Join(",", index.Fields)}";

    private static void DiffNamedItems(List<SchemaDiffChange> changes, string prefix,
        IEnumerable<(string Name, string Key)> before, IEnumerable<(string Name, string Key)> after)
    {
        var previous = before.ToDictionary(item => item.Name);
        var next = after.ToDictionary(item => item.Name);
        foreach (var (name, key) in next)
        {
            if (!previous.TryGetValue(name, out var oldKey))
            {
                changes.Add(new("added", $"{prefix}.{name}"));
            }
            else if (oldKey != key)
            {
                changes.Add(new("changed", $"{prefix}.{name}"));
            }
        }

        foreach (var name in previous.Keys.Where(name => !next.ContainsKey(name)))
        {
            changes.Add(new("removed", $"{prefix}.{name}"));
        }
    }

    private static SchemaCollection ParseSchemaCollection(JsonElement element)
    {
        string String(JsonElement node, string name) =>
            node.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

        bool Flag(JsonElement node, string name) =>
            node.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

        var fields = element.TryGetProperty("fields", out var fieldsElement) && fieldsElement.ValueKind == JsonValueKind.Array
            ? fieldsElement.EnumerateArray().Select(f => new SchemaField(String(f, "name"), String(f, "type"), Flag(f, "required"), Flag(f, "indexed"))).ToArray()
            : [];

        var indexes = element.TryGetProperty("indexes", out var indexesElement) && indexesElement.ValueKind == JsonValueKind.Array
            ? indexesElement.EnumerateArray().Select(i => new SchemaIndex(
                String(i, "name"),
                i.TryGetProperty("fields", out var fieldsList) && fieldsList.ValueKind == JsonValueKind.Array
                    ? [.. fieldsList.EnumerateArray().Select(f => f.GetString() ?? "")]
                    : [])).ToArray()
            : [];

        return new SchemaCollection(
            String(element, "name"),
            fields,
            indexes,
            HistoryRetentionDays: element.TryGetProperty("historyRetentionDays", out var days)
                && days.ValueKind == JsonValueKind.Number && days.TryGetInt32(out var retention)
                ? retention
                : null);
    }
}

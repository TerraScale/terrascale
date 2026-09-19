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
        Database: "core-primary",
        Collection: "orders",
        DocumentId: "doc_01JC9XS3V5X7Z9B2D4F8H1N5P",
        MergePolicy: "last-writer-wins",
        RetentionDays: 30,
        LimitedByRetention: false,
        Winner: new(
            "ver_01JC9ZH2Q7V3X6Y9B1D4F7GJ5",
            new("node-a", 7),
            "2026-09-14T18:22:41.512Z",
            new(["(node-a, 5)", "(node-b, 4)"], false),
            "update",
            "winner",
            """{ "status": "refunded", "customer": "northwind", "total": 42 }""",
            "Highest HLC among the concurrent writers; the deterministic merge policy picked this dot."),
        Versions:
        [
            new("ver_01JC9ZF1P6T2W5X8A0C3E6FI4", new("node-b", 4), "2026-09-14T18:21:09.004Z", new([], false), "create", "version", """{ "status": "active", "customer": "northwind", "total": 42 }""", "Initial write; no causal context recorded."),
            new("ver_01JC9ZG2Q7U3X6Y9B1D4E7H2I", new("node-a", 5), "2026-09-14T18:21:55.338Z", new(["(node-b, 4)"], false), "patch", "version", """{ "status": "paid", "customer": "northwind", "total": 42 }""", "Patch carried the create dot; applied cleanly."),
            new("ver_01JC9ZH2Q7V3X6Y9B1D4F7GJ5", new("node-a", 7), "2026-09-14T18:22:41.512Z", new(["(node-a, 5)", "(node-b, 4)"], false), "update", "winner", """{ "status": "refunded", "customer": "northwind", "total": 42 }""", "Highest HLC among the concurrent writers; the deterministic merge policy picked this dot."),
        ],
        Losers:
        [
            new("ver_01JC9ZI3R8W4Y7Z1C2E5F8J3K", new("node-c", 6), "2026-09-14T18:22:40.998Z", new(["(node-b, 4)"], false), "update", "loser", """{ "status": "void", "customer": "northwind", "total": 0 }""", "Concurrent update on node-c lost the race by HLC; retained as causal history."),
        ]);

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

        var before = Schema.Collections.Select(c => c.Name).ToHashSet();
        var after = collections.EnumerateArray()
            .Where(c => c.TryGetProperty("name", out _))
            .Select(c => c.GetProperty("name").GetString() ?? "")
            .Where(n => n.Length > 0)
            .ToHashSet();
        var changes = new List<SchemaDiffChange>();
        foreach (var name in after.Where(name => !before.Contains(name)))
        {
            changes.Add(new("added", $"collections.{name}"));
        }

        foreach (var name in before.Where(name => !after.Contains(name)))
        {
            changes.Add(new("removed", $"collections.{name}"));
        }

        PendingSchemaDiff = new SchemaDiff([.. changes], changes.Any(c => c.Kind == "removed"));
        ProposedSchemaJson = proposedJson;
        return null;
    }
}

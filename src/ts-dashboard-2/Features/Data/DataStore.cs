using System.Collections.Immutable;
using System.Text.Json;
using TerraScale.Dashboard2.Features.Data;

namespace TerraScale.Dashboard2;

// Store extension: seeded demo data + mutations for the Data Explorer pages
public sealed partial class DashboardStore
{
    private ImmutableArray<ExplorerDocument> explorerDocuments =
    [
        new("doc_01JC9XQ1T3V5X7Z9B2D4F7HJ2", """{ "_id": "doc_01JC9XQ1T3V5X7Z9B2D4F7HJ2", "status": "active", "customer": "northwind", "total": 640 }"""),
        new("doc_01JC9XR2U4W6Y8A1C3E6G9K4M", """{ "_id": "doc_01JC9XR2U4W6Y8A1C3E6G9K4M", "status": "active", "customer": "acme", "total": 128 }"""),
        new("doc_01JC9XS3V5X7Z9B2D4F8H1N5P", """{ "_id": "doc_01JC9XS3V5X7Z9B2D4F8H1N5P", "status": "refunded", "customer": "northwind", "total": 42 }"""),
        new("doc_01JC9XT4W6Y8A1C3E7G2L6Q7R", """{ "_id": "doc_01JC9XT4W6Y8A1C3E7G2L6Q7R", "status": "active", "customer": "globex", "total": 902 }"""),
    ];

    public SchemaDocument Schema { get; private set; } = new(
        Collections:
        [
            new("orders",
            [
                new("id", "string", Required: true, Indexed: true),
                new("status", "string", Required: true, Indexed: true),
                new("customer", "string", Required: false, Indexed: false),
                new("total", "number", Required: true, Indexed: false),
                new("createdAt", "string", Required: true, Indexed: false),
            ],
            [new("orders_status_idx", ["status"]), new("orders_id_idx", ["id"])],
            HistoryRetentionDays: 30),
            new("sessions",
            [
                new("id", "string", Required: true, Indexed: true),
                new("startedAt", "string", Required: true, Indexed: false),
                new("principal", "string", Required: false, Indexed: false),
            ],
            [new("sessions_id_idx", ["id"])]),
        ],
        Version: 4,
        UpdatedAt: "2026-09-10T14:22:00Z",
        Migration: new("complete", 12_480, 12_480, "wal-01JC9M2Q7V"));

    public SchemaDiff? PendingSchemaDiff { get; private set; }

    public string ProposedSchemaJson { get; private set; } = "";

    public ImmutableArray<IndexState> Indexes { get; private set; } =
    [
        new("idx_01JC9YA3C5E7G9J1L4N7Q2S5V", "orders_id_idx", ["id"], "active", 42, 0, null, "2026-09-17T22:41:00Z"),
        new("idx_01JC9YB4D6F8H1K3M5P8R3T6W", "orders_status_idx", ["status"], "active", 88, 0, null, "2026-09-17T22:41:00Z"),
        new("idx_01JC9YC5E7G9J2L4N6Q1S8U9X", "orders_customer_total_idx", ["customer", "total"], "building", null, 3_140, "doc_01JC9XS3V5X7Z9B2D4F8H1N5P", null),
    ];

    public ImmutableArray<QueryTemplate> QueryTemplates { get; private set; } =
    [
        new("tpl_01JC9YR6F8H1K3M5P7R2T5W8Z", "SELECT * FROM orders WHERE status = $1 LIMIT 25", 14_820, 3, 44_460, 6),
        new("tpl_01JC9YS7G9J2L4N6Q8S3U6X1A", "SELECT id, total FROM orders WHERE customer = $1 ORDER BY total DESC", 3_206, 9, 28_854, 14),
        new("tpl_01JC9YT8H1K3M5P7R9T4U7Y2B", "SELECT * FROM sessions WHERE startedAt > $1", 918, 12, 11_016, 21),
    ];

    public ImmutableArray<IndexRecommendation> IndexRecommendations { get; private set; } =
    [
        new("rec_01JC9ZZ4N6Q1S4V7X9B2D5G8J", ["customer", "total"], "Template tpl_01JC9YS7G9J2L4N6Q8S3U6X1A scans 3,206 executions without a covering index.", 71, "recommended"),
        new("rec_01JCA0A5P7R2T5W8Y1C3E6H9K", ["startedAt"], "Template tpl_01JC9YT8H1K3M5P7R9T4U7Y2B filters on an unindexed column.", 44, "applied"),
    ];

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
        database is "core-primary" && collection is "orders" ? explorerDocuments : [];

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
            explorerDocuments = explorerDocuments.Remove(explorerDocuments.FirstOrDefault(d => d.Id == id) ?? new("", ""));
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
        explorerDocuments = operation switch
        {
            "create" => explorerDocuments.Add(new(documentId, normalized)),
            _ => explorerDocuments.Select(d => d.Id == documentId ? new ExplorerDocument(documentId, normalized) : d).ToImmutableArray(),
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

using System.Text.Json;
using System.Text.RegularExpressions;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.DotNet.Tests.TerraBase;

public sealed class TerraBasePublicInventoryTests
{
    private const string ProjectId = "01JA0000000000000000000001";
    private const string EntityId = "01JA0000000000000000000002";

    [Test]
    public async Task Every_public_source_route_has_an_SDK_call_and_no_route_is_dropped()
    {
        using var inventory = JsonDocument.Parse(TerraBaseIntegrationFixture.ReadContract("current-public-routes.json"));
        await Assert.That(inventory.RootElement.GetProperty("serverCommit").GetString()).IsEqualTo("e7fa366ddb289f5612c93d316b316fb92c568151");
        var expected = inventory.RootElement.GetProperty("operations").EnumerateArray().ToDictionary(r => r.GetProperty("operation").GetString()!, r => r);
        await Assert.That(expected.Count).IsEqualTo(91);
        (string Method, string Path)? sent = null;
        using var handler = new TerraBaseTestHandler((request, _) =>
        {
            sent = (request.Method.Method, request.RequestUri!.AbsolutePath);
            throw new HttpRequestException("recorded");
        });
        using var http = new HttpClient(handler);
        var origin = new Uri("https://api.example.test");
        var documents = new TerraBaseDocumentsClient(http, new TerraBaseDocumentsClientOptions { BaseAddress = origin, ApiKey = "fake-token" });
        var db = documents.Database(ProjectId, "app");
        var project = documents.Project(ProjectId);
        var platform = new TerraBasePlatformClient(http, new TerraBasePlatformClientOptions { BaseAddress = origin, AccessTokenProvider = _ => ValueTask.FromResult("fake-token") });
        var health = new TerraBaseHealthClient(http, origin);
        var shapes = new TerraBaseShapeClient(http, new TerraBaseShapeClientOptions { BaseAddress = origin, ApiKey = "fake-token" });
        using var body = JsonDocument.Parse("{}");
        var json = body.RootElement;
        (string Operation, Func<Task> Call)[] calls =
        [
            ("applyIndexRecommendation", () => db.ApplyIndexRecommendationAsync("items:title")),
            ("backupz", () => health.GetBackupHealthAsync()),
            ("branchDatabase", () => db.BranchAsync("copy")),
            ("cancelImport", () => db.CancelImportAsync()),
            ("checkSql", () => db.CheckSqlAsync("SELECT * FROM items LIMIT 1")),
            ("createBackup", () => db.CreateBackupAsync()),
            ("createChangeWebhook", () => db.CreateChangeWebhookAsync("items", "https://receiver.example/events")),
            ("listChangeWebhooks", () => db.ListChangeWebhooksAsync("items")),
            ("deleteChangeWebhook", () => db.DeleteChangeWebhookAsync("items", EntityId)),
            ("listChangeWebhookDeliveries", () => db.ListChangeWebhookDeliveriesAsync("items", EntityId)),
            ("invokeFunction", () => db.InvokeFunctionAsync("hello")),
            ("listFunctionLogs", () => db.ListFunctionLogsAsync("hello")),
            ("listFunctionDeliveries", () => db.ListFunctionDeliveriesAsync("hello")),
            ("listFunctions", () => db.ListFunctionsAsync()),
            ("deployFunction", () => db.DeployFunctionAsync("hello", new("function handler(event) { return event; }"))),
            ("getFunction", () => db.GetFunctionAsync("hello")),
            ("deleteFunction", () => db.DeleteFunctionAsync("hello")),
            ("createCollection", () => db.CreateCollectionAsync("items")),
            ("createDatabase", () => project.CreateDatabaseAsync("app")),
            ("createDocument", () => db.CreateDocumentAsync("items", json)),
            ("createImport", () => db.CreateImportAsync(TerraBaseImportFormat.Sqlite, 1, new string('0', 64))),
            ("createIndex", () => db.CreateIndexAsync("items", ["title"])),
            ("createSavedQuery", () => project.CreateSavedQueryAsync("recent", "SELECT * FROM items LIMIT 1")),
            ("declareCollectionSchema", () => db.DeclareSchemaAsync("items", new())),
            ("deleteBackupDestination", () => project.DeleteBackupDestinationAsync()),
            ("deleteDatabase", () => project.DeleteDatabaseAsync("app")),
            ("deleteCollectionTtl", () => db.ClearCollectionTtlAsync("items")),
            ("deleteDeclaredCollectionSchema", () => db.DeleteDeclaredSchemaAsync("items")),
            ("deleteDocument", () => db.DeleteDocumentAsync("items", "01JA00000000000000000000D1")),
            ("deleteSavedQuery", () => project.DeleteSavedQueryAsync(EntityId)),
            ("dropCollection", () => db.DropCollectionAsync("items")),
            ("dropIndex", () => db.DropIndexAsync("items_title")),
            ("explainSql", () => db.ExplainSqlAsync("SELECT * FROM items LIMIT 1")),
            ("getAutoIndexPolicy", () => db.GetAutoIndexPolicyAsync("items")),
            ("getBackupDestination", () => project.GetBackupDestinationAsync()),
            ("getBackupPolicy", () => db.GetBackupPolicyAsync()),
            ("getCollection", () => db.GetCollectionAsync("items")),
            ("getCollectionSchema", () => db.GetInferredSchemaAsync("items")),
            ("getCollectionTtl", () => db.GetCollectionTtlAsync("items")),
            ("getDatabase", () => project.GetDatabaseAsync("app")),
            ("getDatabaseAnalytics", () => db.GetAnalyticsAsync()),
            ("getDatabaseReplication", () => db.GetReplicationAsync()),
            ("getDatabaseSchema", () => db.ListCollectionStatsAsync()),
            ("getDatabaseUsage", () => db.GetUsageAsync()),
            ("getDeclaredCollectionSchema", () => db.GetDeclaredSchemaAsync("items")),
            ("getDocument", () => db.GetDocumentAsync("items", "01JA00000000000000000000D1")),
            ("getImport", () => db.GetImportAsync()),
            ("getPlatformDocument", () => platform.GetDocumentAsync("ts-auth", "items", "01JA00000000000000000000D1")),
            ("getProjectAnalytics", () => project.GetAnalyticsAsync()),
            ("getProjectReplication", () => project.GetReplicationAsync()),
            ("getProjectUsage", () => project.GetUsageAsync()),
            ("getRestoreWindow", () => db.GetRestoreWindowAsync()),
            ("getSavedQuery", () => project.GetSavedQueryAsync(EntityId)),
            ("getShape", () => FirstAsync(shapes.SubscribeAsync(ProjectId, "app", "items"))),
            ("getShapeKey", () => shapes.GetEpochKeyAsync(ProjectId, "app", "items", "h1-0123456789abcdef", 1, EntityId)),
            ("getShapeManifest", () => shapes.GetManifestAsync(ProjectId, "app", "items", "h1-0123456789abcdef", "2")),
            ("listBackups", () => db.ListBackupsAsync()),
            ("listCollections", () => db.ListCollectionsAsync()),
            ("listDatabases", () => project.ListDatabasesAsync()),
            ("listDocuments", () => db.ListDocumentsAsync("items")),
            ("listIndexRecommendations", () => db.ListIndexRecommendationsAsync()),
            ("listIndexes", () => db.ListIndexesAsync()),
            ("listPlatformCollections", () => platform.ListCollectionsAsync("ts-auth")),
            ("listPlatformDatabases", () => platform.ListDatabasesAsync()),
            ("listPlatformDocuments", () => platform.ListDocumentsAsync("ts-auth", "items")),
            ("listQueryShapes", () => db.ListQueryShapesAsync()),
            ("listSavedQueries", () => project.ListSavedQueriesAsync()),
            ("patchDocument", () => db.PatchDocumentAsync("items", "01JA00000000000000000000D1", json)),
            ("planPointInTime", () => db.PlanPointInTimeAsync(DateTimeOffset.UnixEpoch)),
            ("putCollectionTtl", () => db.SetCollectionTtlAsync("items", "/expiresAt")),
            ("putDeclaredCollectionSchema", () => db.PutDeclaredSchemaAsync("items", json)),
            ("putDocument", () => db.PutDocumentAsync("items", "01JA00000000000000000000D1", json)),
            ("queryDocuments", () => db.QueryAsync("items", new())),
            ("readyz", () => health.GetReadinessAsync()),
            ("replicationHealth", () => health.GetReplicationHealthAsync()),
            ("restoreBackup", () => db.RestoreBackupAsync(EntityId)),
            ("restorePointInTime", () => db.RestorePointInTimeAsync(DateTimeOffset.UnixEpoch)),
            ("resumeImport", () => db.ResumeImportAsync()),
            ("runSql", () => db.RunSqlAsync("SELECT * FROM items LIMIT 1")),
            ("executeSql", () => db.ExecuteSqlAsync("SELECT 1")),
            ("runTransaction", () => db.TransactAsync([TerraBaseTransactionOperation.Put("items", "01JA00000000000000000000D1", json)])),
            ("setAutoIndexPolicy", () => db.SetAutoIndexPolicyAsync("items", false)),
            ("setBackupDestination", () => project.SetBackupDestinationAsync(new("https://s3.example.test", "region", "bucket", "fake-id", "fake-secret", "both"))),
            ("setBackupPolicy", () => db.SetBackupPolicyAsync(null, 7)),
            ("startImport", () => db.StartImportAsync(EntityId)),
            ("status", () => health.GetHealthAsync()),
            ("streamDatabaseChanges", () => FirstAsync(db.StreamChangesAsync())),
            ("updateDatabase", () => project.SetDatabaseRegionsAsync("app", [])),
            ("updateImport", () => db.UpdateImportAsync(new())),
            ("updateSavedQuery", () => project.UpdateSavedQueryAsync(EntityId, "recent", "SELECT * FROM items LIMIT 1")),
            ("uploadImportSource", () => db.UploadImportSourceAsync(0, new byte[] { 1 })),
        ];
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (operation, call) in calls)
        {
            sent = null;
            await Assert.ThrowsAsync<HttpRequestException>(call);
            if (sent is not { } wire) throw new InvalidOperationException(operation + " sent no request");
            var route = expected[operation];
            await Assert.That(wire.Method).IsEqualTo(route.GetProperty("method").GetString());
            var pattern = "^" + Regex.Replace(Regex.Escape(route.GetProperty("path").GetString()!), @"\\\{[a-z]+}", "[^/]+") + "$";
            await Assert.That(Regex.IsMatch(wire.Path, pattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1))).IsTrue().Because(operation);
            await Assert.That(used.Add(operation)).IsTrue();
        }
        await Assert.That(expected.Keys.Except(used)).IsEmpty();
    }

    private static async Task FirstAsync<T>(IAsyncEnumerable<T> events)
    {
        await using var enumerator = events.GetAsyncEnumerator();
        await enumerator.MoveNextAsync();
    }
}

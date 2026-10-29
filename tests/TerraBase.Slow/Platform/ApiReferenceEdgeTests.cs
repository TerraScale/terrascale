using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using TerraBase.Fast.Documents;
using TerraBase.Hosting;
using TerraBase.Primitives;
using TerraBase.Testing;

namespace TerraBase.Fast.Platform;

public sealed class ApiReferenceEdgeTests
{
    private const string Operations = """
        DELETE /v1/projects/{project}/backup-destination TsAuth(databases:write) deleteBackupDestination Backups
        DELETE /v1/projects/{project}/buckets/{bucket} TsAuth(documents:write) deleteFileBucket File storage
        DELETE /v1/projects/{project}/buckets/{bucket}/files/{file} TsAuth(documents:write) deleteBucketFile File storage
        DELETE /v1/projects/{project}/databases/{database} TsAuth(databases:write) deleteDatabase Databases
        DELETE /v1/projects/{project}/databases/{database}/collections/{collection} TsAuth(documents:write) dropCollection Collections
        DELETE /v1/projects/{project}/databases/{database}/collections/{collection}/documents/by-key TsAuth(documents:write) deleteDocument Documents
        DELETE /v1/projects/{project}/databases/{database}/collections/{collection}/schema TsAuth(documents:write) deleteDeclaredCollectionSchema Collections
        DELETE /v1/projects/{project}/databases/{database}/collections/{collection}/ttl TsAuth(documents:write) deleteCollectionTtl Collections
        DELETE /v1/projects/{project}/databases/{database}/functions/{function} TsAuth(documents:read,documents:write) deleteFunction Functions
        DELETE /v1/projects/{project}/databases/{database}/if-id/{expectedId} TsAuth(databases:write) deleteDatabaseIfId Databases
        DELETE /v1/projects/{project}/databases/{database}/indexes/{index} TsAuth(databases:write) dropIndex Indexes
        DELETE /v1/projects/{project}/saved-queries/{query} TsAuth(databases:write) deleteSavedQuery Queries
        GET /backupz anonymous backupz Health
        GET /healthz anonymous healthz Health
        GET /readyz anonymous readyz Health
        GET /v1/platform/databases TsAuth(platform_role=admin) listPlatformDatabases Platform
        GET /v1/platform/databases/{database}/collections TsAuth(platform_role=admin) listPlatformCollections Platform
        GET /v1/platform/databases/{database}/collections/{collection}/documents TsAuth(platform_role=admin) listPlatformDocuments Platform
        GET /v1/platform/databases/{database}/collections/{collection}/documents/by-key TsAuth(platform_role=admin) getPlatformDocument Platform
        GET /v1/projects/{project}/analytics TsAuth(databases:read) getProjectAnalytics Analytics
        GET /v1/projects/{project}/backup-destination TsAuth(databases:read) getBackupDestination Backups
        GET /v1/projects/{project}/buckets TsAuth(documents:read) listFileBuckets File storage
        GET /v1/projects/{project}/buckets/{bucket}/files TsAuth(documents:read) listBucketFiles File storage
        GET /v1/projects/{project}/databases TsAuth(databases:read) listDatabases Databases
        GET /v1/projects/{project}/databases/{database} TsAuth(databases:read) getDatabase Databases
        GET /v1/projects/{project}/databases/{database}/analytics TsAuth(databases:read) getDatabaseAnalytics Analytics
        GET /v1/projects/{project}/databases/{database}/backup-policy TsAuth(databases:read) getBackupPolicy Backups
        GET /v1/projects/{project}/databases/{database}/backups TsAuth(databases:read) listBackups Backups
        GET /v1/projects/{project}/databases/{database}/changes TsAuth(documents:read) streamDatabaseChanges Documents
        GET /v1/projects/{project}/databases/{database}/collections TsAuth(documents:read) listCollections Collections
        GET /v1/projects/{project}/databases/{database}/collections/{collection} TsAuth(documents:read) getCollection Collections
        GET /v1/projects/{project}/databases/{database}/collections/{collection}/documents TsAuth(documents:read) listDocuments Documents
        GET /v1/projects/{project}/databases/{database}/collections/{collection}/documents/by-key TsAuth(documents:read) getDocument Documents
        GET /v1/projects/{project}/databases/{database}/collections/{collection}/rules TsAuth(documents:read) getCollectionRules Collections
        GET /v1/projects/{project}/databases/{database}/collections/{collection}/schema TsAuth(documents:read) getDeclaredCollectionSchema Collections
        GET /v1/projects/{project}/databases/{database}/collections/{collection}/shape TsAuth(documents:read) getShape Live
        GET /v1/projects/{project}/databases/{database}/collections/{collection}/shape/keys TsAuth(documents:read) getShapeKey Live
        GET /v1/projects/{project}/databases/{database}/collections/{collection}/shape/manifest TsAuth(documents:read) getShapeManifest Live
        GET /v1/projects/{project}/databases/{database}/collections/{collection}/ttl TsAuth(documents:read) getCollectionTtl Collections
        GET /v1/projects/{project}/databases/{database}/export TsAuth(documents:read) exportDatabase Documents
        GET /v1/projects/{project}/databases/{database}/export-schedule TsAuth(databases:read) getExportSchedule Backups
        GET /v1/projects/{project}/databases/{database}/functions TsAuth(documents:read,documents:write) listFunctions Functions
        GET /v1/projects/{project}/databases/{database}/functions/{function} TsAuth(documents:read,documents:write) getFunction Functions
        GET /v1/projects/{project}/databases/{database}/functions/{function}/logs TsAuth(documents:read,documents:write) listFunctionLogs Functions
        GET /v1/projects/{project}/databases/{database}/import TsAuth(databases:read) getImport Imports
        GET /v1/projects/{project}/databases/{database}/index-recommendations TsAuth(databases:read) listIndexRecommendations Indexes
        GET /v1/projects/{project}/databases/{database}/indexes TsAuth(databases:read) listIndexes Indexes
        GET /v1/projects/{project}/databases/{database}/logs TsAuth(databases:read) listDatabaseLogs Logs
        GET /v1/projects/{project}/databases/{database}/point-in-time TsAuth(databases:read) getRestoreWindow Backups
        GET /v1/projects/{project}/databases/{database}/query-shapes TsAuth(databases:read) listQueryShapes Queries
        GET /v1/projects/{project}/databases/{database}/schema TsAuth(databases:read) getDatabaseSchema Schema
        GET /v1/projects/{project}/databases/{database}/schema/{collection} TsAuth(databases:read) getCollectionSchema Schema
        GET /v1/projects/{project}/databases/{database}/usage TsAuth(databases:read) getDatabaseUsage Usage
        GET /v1/projects/{project}/metrics TsAuth(databases:read) getProjectMetrics Analytics
        GET /v1/projects/{project}/saved-queries TsAuth(databases:read) listSavedQueries Queries
        GET /v1/projects/{project}/saved-queries/{query} TsAuth(databases:read) getSavedQuery Queries
        GET /v1/projects/{project}/usage TsAuth(databases:read) getProjectUsage Usage
        GET /v1/regions anonymous listRegions Regions
        PATCH /v1/projects/{project}/databases/{database} TsAuth(databases:write) updateDatabase Databases
        PATCH /v1/projects/{project}/databases/{database}/collections/{collection}/documents/by-key TsAuth(documents:write) patchDocument Documents
        PATCH /v1/projects/{project}/databases/{database}/import TsAuth(databases:write,documents:write) updateImport Imports
        POST /v1/projects/{project}/buckets TsAuth(documents:write) createFileBucket File storage
        POST /v1/projects/{project}/buckets/{bucket}/files/upload TsAuth(documents:write) createFileUpload File storage
        POST /v1/projects/{project}/buckets/{bucket}/files/{file}/complete TsAuth(documents:write) completeFileUpload File storage
        POST /v1/projects/{project}/buckets/{bucket}/files/{file}/download TsAuth(documents:read) createFileDownload File storage
        POST /v1/projects/{project}/databases TsAuth(databases:write) createDatabase Databases
        POST /v1/projects/{project}/databases/{database}/backups TsAuth(databases:write) createBackup Backups
        POST /v1/projects/{project}/databases/{database}/backups/{backup}/restore TsAuth(databases:write) restoreBackup Backups
        POST /v1/projects/{project}/databases/{database}/branches TsAuth(databases:write) branchDatabase Databases
        POST /v1/projects/{project}/databases/{database}/branches/if-id/{expectedId} TsAuth(databases:write) branchDatabaseIfId Databases
        POST /v1/projects/{project}/databases/{database}/collections TsAuth(documents:write) createCollection Collections
        POST /v1/projects/{project}/databases/{database}/collections/{collection}/documents TsAuth(documents:write) createDocument Documents
        POST /v1/projects/{project}/databases/{database}/collections/{collection}/query TsAuth(documents:read) queryDocuments Queries
        POST /v1/projects/{project}/databases/{database}/collections/{collection}/schema/preview TsAuth(documents:read,documents:write) previewCollectionSchema Collections
        POST /v1/projects/{project}/databases/{database}/functions/{function}/invoke TsAuth(documents:read) invokeFunction Functions
        POST /v1/projects/{project}/databases/{database}/import TsAuth(databases:write,documents:write) createImport Imports
        POST /v1/projects/{project}/databases/{database}/import/cancel TsAuth(databases:write) cancelImport Imports
        POST /v1/projects/{project}/databases/{database}/import/resume TsAuth(databases:write,documents:write) resumeImport Imports
        POST /v1/projects/{project}/databases/{database}/import/start TsAuth(databases:write,documents:write) startImport Imports
        POST /v1/projects/{project}/databases/{database}/index-recommendations/{recommendation}/apply TsAuth(databases:write) applyIndexRecommendation Indexes
        POST /v1/projects/{project}/databases/{database}/indexes TsAuth(databases:write) createIndex Indexes
        POST /v1/projects/{project}/databases/{database}/point-in-time/plan TsAuth(databases:read) planPointInTime Backups
        POST /v1/projects/{project}/databases/{database}/point-in-time/recover TsAuth(databases:write,documents:write) recoverHistoricalRecords Backups
        POST /v1/projects/{project}/databases/{database}/point-in-time/recovery-preview TsAuth(databases:write,documents:read) previewSelectiveRecovery Backups
        POST /v1/projects/{project}/databases/{database}/point-in-time/restore TsAuth(databases:write) restorePointInTime Backups
        POST /v1/projects/{project}/databases/{database}/sql TsAuth(databases:read) runSql Queries
        POST /v1/projects/{project}/databases/{database}/sql/check TsAuth(databases:read) checkSql Queries
        POST /v1/projects/{project}/databases/{database}/sql/execute TsAuth(documents:read) executeSql Queries
        POST /v1/projects/{project}/databases/{database}/sql/explain TsAuth(databases:read) explainSql Queries
        POST /v1/projects/{project}/databases/{database}/transactions TsAuth(documents:write) runTransaction Transactions
        POST /v1/projects/{project}/saved-queries TsAuth(databases:write) createSavedQuery Queries
        PUT /v1/projects/{project}/backup-destination TsAuth(databases:write) setBackupDestination Backups
        PUT /v1/projects/{project}/buckets/{bucket}/rules TsAuth(documents:write) setFileBucketRules File storage
        PUT /v1/projects/{project}/databases/{database}/backup-policy TsAuth(databases:write) setBackupPolicy Backups
        PUT /v1/projects/{project}/databases/{database}/collections/{collection}/documents/by-key TsAuth(documents:write) putDocument Documents
        PUT /v1/projects/{project}/databases/{database}/collections/{collection}/rules TsAuth(documents:write) putCollectionRules Collections
        PUT /v1/projects/{project}/databases/{database}/collections/{collection}/schema TsAuth(documents:write) putDeclaredCollectionSchema Collections
        PUT /v1/projects/{project}/databases/{database}/collections/{collection}/ttl TsAuth(documents:write) putCollectionTtl Collections
        PUT /v1/projects/{project}/databases/{database}/export-schedule TsAuth(databases:write) setExportSchedule Backups
        PUT /v1/projects/{project}/databases/{database}/functions/{function} TsAuth(documents:read,documents:write) deployFunction Functions
        PUT /v1/projects/{project}/databases/{database}/import/source TsAuth(databases:write,documents:write) uploadImportSource Imports
        PUT /v1/projects/{project}/databases/{database}/schema/{collection} TsAuth(databases:write) declareCollectionSchema Schema
        PUT /v1/projects/{project}/saved-queries/{query} TsAuth(databases:write) updateSavedQuery Queries
        """;

    [Test]
    public async Task Readiness_region_pin_errors_have_error_examples_while_unavailable_keeps_the_status_body()
    {
        using var tls = new ClientTls();
        await using var server = await TerraBaseTestServer.StartAsync(settings =>
        {
            tls.Apply(settings);
            settings["region"] = "gru-1";
        });
        using var client = tls.CreateClient(server.ClientAddress!);
        using var request = new HttpRequestMessage(HttpMethod.Get, Uri("/readyz"));
        request.Headers.Add("X-TerraScale-Region-Pin", "sfo-1");
        using var response = await client.SendAsync(request);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.MisdirectedRequest);
        await Assert.That(response.Content.Headers.ContentType!.MediaType).IsEqualTo("application/json");
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var code = body.RootElement.GetProperty("error").GetProperty("code").GetString();
        await Assert.That(code).IsEqualTo("region_pin_mismatch");

        using var reference = await client.GetAsync(Uri("/openapi/v1.json"));
        await Assert.That(reference.StatusCode).IsEqualTo(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await reference.Content.ReadAsStringAsync());
        var responses = document.RootElement.GetProperty("paths").GetProperty("/readyz").GetProperty("get").GetProperty("responses");
        var misdirected = responses.GetProperty("421");
        await Assert.That(misdirected.GetProperty("description").GetString()).Contains(code!);
        var error = misdirected.GetProperty("content").GetProperty("application/json");
        var schema = error.GetProperty("schema");
        await Assert.That(schema.GetProperty("required").EnumerateArray().Select(item => item.GetString())).Contains("error");
        var details = schema.GetProperty("properties").GetProperty("error");
        await Assert.That(details.GetProperty("required").EnumerateArray().Select(item => item.GetString())).Contains("code");
        await Assert.That(details.GetProperty("properties").GetProperty("code").GetProperty("type").GetString()).IsEqualTo("string");
        await Assert.That(error.GetProperty("example").GetProperty("error").GetProperty("code").GetString()).IsEqualTo(code);
        await Assert.That(error.GetProperty("example").TryGetProperty("status", out _)).IsFalse();
        var unavailable = responses.GetProperty("503").GetProperty("content").GetProperty("application/json");
        await Assert.That(unavailable.GetProperty("schema").GetRawText()).Contains("StatusBody");
        await Assert.That(unavailable.GetProperty("example").GetProperty("status").GetString()).IsEqualTo("unavailable");
        await Assert.That(unavailable.GetProperty("example").TryGetProperty("error", out _)).IsFalse();
    }

    [Test]
    public async Task Patch_conflict_examples_and_descriptions_match_project_JWT_responses()
    {
        await using var api = await DocumentApi.StartAsync();
        var id = DocumentApi.NewId();
        await Assert.That((await api.PutAsync(id, new { n = 1 })).Status).IsEqualTo(HttpStatusCode.Created);
        using var owner = api.Management.As(api.Tenant.User);
        using var client = api.Management.WithToken(api.Management.Issuer.Mint(api.Tenant.User, token =>
        {
            token.Claims["project_id"] = api.Tenant.ProjectId;
            token.Claims["scope"] = "documents:write";
        }));
        var ops = new object[] { new { op = "append", path = "/n", value = 1 } };
        var patch = await DocumentApi.SendAsync(client, HttpMethod.Patch, api.Document(id),
            JsonContent.Create(new { ops }, mediaType: new MediaTypeHeaderValue("application/vnd.terrabase.ops+json")));
        var transaction = await DocumentApi.SendAsync(client, HttpMethod.Post, api.Transactions,
            JsonContent.Create(new { ops = new object[] { new { op = "patch", collection = "items", id, ops } } }));
        using var reference = await client.GetAsync(Uri("/openapi/v1.json"));
        await Assert.That(reference.StatusCode).IsEqualTo(HttpStatusCode.OK);
        using var json = JsonDocument.Parse(await reference.Content.ReadAsStringAsync());
        var operations = json.RootElement.GetProperty("paths").EnumerateObject()
            .SelectMany(path => path.Value.EnumerateObject()
                .Where(operation => operation.Value.TryGetProperty("responses", out _))
                .Select(operation => operation.Value));

        foreach (var (operationId, result) in new[] { ("patchDocument", patch), ("runTransaction", transaction) })
        {
            await Assert.That((result.Status, result.Code)).IsEqualTo((HttpStatusCode.Conflict, "patch_conflict"));
            var documented = operations.Single(operation => operation.GetProperty("operationId").GetString() == operationId)
                .GetProperty("responses").GetProperty("409");
            await Assert.That(documented.GetProperty("content").GetProperty("application/json").GetProperty("example")
                .GetProperty("error").GetProperty("code").GetString()).IsEqualTo(result.Code).Because(operationId);
            await Assert.That(documented.GetProperty("description").GetString()).Contains(result.Code!).Because(operationId);
        }
    }

    private static string Security(JsonElement operation) =>
        operation.TryGetProperty("security", out var security)
            ? string.Join(' ', security.EnumerateArray().SelectMany(requirement => requirement.EnumerateObject()).Select(scheme =>
                $"{scheme.Name}({string.Join(',', (operation.TryGetProperty("x-required-scopes", out var scopes) ? scopes : operation.GetProperty("x-required-claims")).EnumerateArray().Select(scope => scope.GetString()))})"))
            : "anonymous";

    private static Uri Uri(string path) => new(path, UriKind.Relative);
}

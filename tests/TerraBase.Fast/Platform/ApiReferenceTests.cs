using System.Net;
using System.Text.Json;
using TerraBase.Hosting;
using TerraBase.Primitives;
using TerraBase.Testing;

namespace TerraBase.Fast.Platform;

public sealed class ApiReferenceTests
{
    private const string Operations = """
        DELETE /v1/projects/{project}/backup-destination TsAuth(databases:write) deleteBackupDestination Backups
        DELETE /v1/projects/{project}/buckets/{bucket} TsAuth(sql:write) deleteFileBucket File storage
        DELETE /v1/projects/{project}/buckets/{bucket}/files/{file} TsAuth(sql:write) deleteBucketFile File storage
        DELETE /v1/projects/{project}/databases/{database} TsAuth(databases:write) deleteDatabase Databases
        DELETE /v1/projects/{project}/databases/{database}/functions/{function} TsAuth(sql:read,sql:write) deleteFunction Functions
        DELETE /v1/projects/{project}/databases/{database}/if-id/{expectedId} TsAuth(databases:write) deleteDatabaseIfId Databases
        DELETE /v1/projects/{project}/databases/{database}/tables/{table}/read-grant TsAuth(databases:write) deleteReadGrant Live
        DELETE /v1/projects/{project}/databases/{database}/ttl TsAuth(databases:write) clearDatabaseTtl Databases
        GET /backupz anonymous backupz Health
        GET /healthz anonymous healthz Health
        GET /readyz anonymous readyz Health
        GET /v1/platform/databases TsAuth(platform_role=admin) listPlatformDatabases Platform
        GET /v1/projects/{project}/analytics TsAuth(databases:read) getProjectAnalytics Analytics
        GET /v1/projects/{project}/backup-destination TsAuth(databases:read) getBackupDestination Backups
        GET /v1/projects/{project}/buckets TsAuth(sql:read) listFileBuckets File storage
        GET /v1/projects/{project}/buckets/{bucket}/files TsAuth(sql:read) listBucketFiles File storage
        GET /v1/projects/{project}/databases TsAuth(databases:read) listDatabases Databases
        GET /v1/projects/{project}/databases/{database} TsAuth(databases:read) getDatabase Databases
        GET /v1/projects/{project}/databases/{database}/analytics TsAuth(databases:read) getDatabaseAnalytics Analytics
        GET /v1/projects/{project}/databases/{database}/backup-policy TsAuth(databases:read) getBackupPolicy Backups
        GET /v1/projects/{project}/databases/{database}/backups TsAuth(databases:read) listBackups Backups
        GET /v1/projects/{project}/databases/{database}/functions TsAuth(sql:read,sql:write) listFunctions Functions
        GET /v1/projects/{project}/databases/{database}/functions/{function} TsAuth(sql:read,sql:write) getFunction Functions
        GET /v1/projects/{project}/databases/{database}/functions/{function}/logs TsAuth(sql:read,sql:write) listFunctionLogs Functions
        GET /v1/projects/{project}/databases/{database}/logs TsAuth(databases:read) listDatabaseLogs Logs
        GET /v1/projects/{project}/databases/{database}/point-in-time TsAuth(databases:read) getRestoreWindow Backups
        GET /v1/projects/{project}/databases/{database}/read-grants TsAuth(databases:read) listReadGrants Live
        GET /v1/projects/{project}/databases/{database}/regions TsAuth(databases:read) getDatabaseRegions Databases
        GET /v1/projects/{project}/databases/{database}/sql/ws TsAuth(sql:read) connectSql Queries
        GET /v1/projects/{project}/databases/{database}/tables/{table}/read-grant TsAuth(databases:read) getReadGrant Live
        GET /v1/projects/{project}/databases/{database}/tables/{table}/shape TsAuth(sql:read) getShape Live
        GET /v1/projects/{project}/databases/{database}/tables/{table}/shape/keys TsAuth(sql:read) getShapeKey Live
        GET /v1/projects/{project}/databases/{database}/tables/{table}/shape/manifest TsAuth(sql:read) getShapeManifest Live
        GET /v1/projects/{project}/databases/{database}/usage TsAuth(databases:read) getDatabaseUsage Usage
        GET /v1/projects/{project}/metrics TsAuth(databases:read) getProjectMetrics Analytics
        GET /v1/projects/{project}/usage TsAuth(databases:read) getProjectUsage Usage
        GET /v1/regions anonymous listRegions Regions
        PATCH /v1/projects/{project}/databases/{database} TsAuth(databases:write) updateDatabase Databases
        POST /v1/projects/{project}/buckets TsAuth(sql:write) createFileBucket File storage
        POST /v1/projects/{project}/buckets/{bucket}/files/upload TsAuth(sql:write) createFileUpload File storage
        POST /v1/projects/{project}/buckets/{bucket}/files/{file}/complete TsAuth(sql:write) completeFileUpload File storage
        POST /v1/projects/{project}/buckets/{bucket}/files/{file}/download TsAuth(sql:read) createFileDownload File storage
        POST /v1/projects/{project}/databases TsAuth(databases:write) createDatabase Databases
        POST /v1/projects/{project}/databases/{database}/backups TsAuth(databases:write) createBackup Backups
        POST /v1/projects/{project}/databases/{database}/backups/{backup}/restore TsAuth(databases:write) restoreBackup Backups
        POST /v1/projects/{project}/databases/{database}/branches TsAuth(databases:write) branchDatabase Databases
        POST /v1/projects/{project}/databases/{database}/branches/if-id/{expectedId} TsAuth(databases:write) branchDatabaseIfId Databases
        POST /v1/projects/{project}/databases/{database}/functions/{function}/invoke TsAuth(sql:read) invokeFunction Functions
        POST /v1/projects/{project}/databases/{database}/move-home TsAuth(databases:write) moveDatabaseHome Databases
        POST /v1/projects/{project}/databases/{database}/point-in-time/plan TsAuth(databases:read) planPointInTime Backups
        POST /v1/projects/{project}/databases/{database}/point-in-time/restore TsAuth(databases:write) restorePointInTime Backups
        POST /v1/projects/{project}/databases/{database}/sql/execute TsAuth(sql:read) executeSql Queries
        PUT /v1/projects/{project}/backup-destination TsAuth(databases:write) setBackupDestination Backups
        PUT /v1/projects/{project}/buckets/{bucket}/rules TsAuth(sql:write) setFileBucketRules File storage
        PUT /v1/projects/{project}/databases/{database}/backup-policy TsAuth(databases:write) setBackupPolicy Backups
        PUT /v1/projects/{project}/databases/{database}/functions/{function} TsAuth(sql:read,sql:write) deployFunction Functions
        PUT /v1/projects/{project}/databases/{database}/tables/{table}/read-grant TsAuth(databases:write) putReadGrant Live
        PUT /v1/projects/{project}/databases/{database}/ttl TsAuth(databases:write) setDatabaseTtl Databases
        """;

    [Test]
    public async Task The_openapi_document_lists_every_public_operation_with_its_security_and_errors()
    {
        using var tls = new ClientTls();
        await using var server = await TerraBaseTestServer.StartAsync(settings =>
        {
            tls.Apply(settings);
            settings["region"] = "gru-1";
        });
        using var client = tls.CreateClient(server.ClientAddress!);

        using var response = await client.GetAsync(Uri("/openapi/v1.json"));
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var document = json.RootElement;
        var operations = document.GetProperty("paths").EnumerateObject()
            .SelectMany(path => path.Value.EnumerateObject()
                .Where(operation => operation.Value.TryGetProperty("responses", out _))
                .Select(operation => (Line: $"{operation.Name.ToUpperInvariant()} {path.Name} {Security(operation.Value)} {operation.Value.GetProperty("operationId").GetString()} {operation.Value.GetProperty("tags")[0].GetString()}", Operation: operation.Value)))
            .ToList();

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(document.GetProperty("paths").EnumerateObject().Any(path =>
            path.Name.StartsWith("/management/", StringComparison.OrdinalIgnoreCase)
            || path.Name.StartsWith("/v1/management/", StringComparison.OrdinalIgnoreCase))).IsFalse();
        await Assert.That(document.GetProperty("info").GetProperty("version").GetString()).IsEqualTo(ServiceVersion.Value);
        var server0 = document.GetProperty("servers")[0];
        await Assert.That(server0.GetProperty("url").GetString()).IsEqualTo("/");
        await Assert.That(server0.GetProperty("description").GetString()).Contains("gru-1");
        var schemes = document.GetProperty("components").GetProperty("securitySchemes");
        await Assert.That(schemes.EnumerateObject().Count()).IsEqualTo(1);
        await Assert.That(schemes.GetProperty("TsAuth").GetProperty("type").GetString()).IsEqualTo("http");
        await Assert.That(schemes.GetProperty("TsAuth").GetProperty("scheme").GetString()).IsEqualTo("bearer");
        await Assert.That(schemes.GetProperty("TsAuth").GetProperty("bearerFormat").GetString()).IsEqualTo("JWT");
        await Assert.That(schemes.GetProperty("TsAuth").GetProperty("description").GetString()).Contains("project access token requests receive 401");
        await Assert.That(string.Join(',', document.GetProperty("x-tagGroups").EnumerateArray().Select(group => group.GetProperty("name").GetString())))
            .IsEqualTo("Management,Data,System");
        await Assert.That(string.Join('\n', operations.Select(operation => operation.Line).Order(StringComparer.Ordinal)))
            .IsEqualTo(Operations.ReplaceLineEndings("\n"));
        foreach (var (line, operation) in operations)
        {
            await Assert.That(operation.GetProperty("tags").GetArrayLength()).IsEqualTo(1).Because(line);
            await Assert.That(string.IsNullOrEmpty(operation.GetProperty("summary").GetString())).IsFalse().Because(line);
            await Assert.That(string.IsNullOrEmpty(operation.GetProperty("description").GetString())).IsFalse().Because(line);
            if (operation.TryGetProperty("requestBody", out var requestBody))
            {
                foreach (var media in requestBody.GetProperty("content").EnumerateObject())
                {
                    await Assert.That(media.Value.TryGetProperty("example", out _)).IsTrue().Because(line);
                }
            }

            var responses = operation.GetProperty("responses").EnumerateObject().ToList();
            if (operation.GetProperty("operationId").GetString() is "getDatabaseUsage" or "getProjectUsage")
            {
                await Assert.That(operation.GetProperty("responses").GetProperty("400").GetProperty("description").GetString()).Contains("invalid_query");
            }

            if (operation.GetProperty("operationId").GetString() is "createDatabase" or "branchDatabase" or "branchDatabaseIfId" or "restoreBackup"
                or "createBackup")
            {
                var success = responses.First(status => status.Name.StartsWith('2')).Value;
                var example = success.GetProperty("content").GetProperty("application/json").GetProperty("example");
                await Assert.That(EntityIds.IsValid(example.GetProperty("id").GetString())).IsTrue().Because(line);
            }
            var succeeds = operation.GetProperty("operationId").GetString() == "connectSql"
                ? responses.Any(status => status.Name == "101")
                : responses.Any(status => status.Name.StartsWith('2'));
            await Assert.That(succeeds).IsTrue().Because(line);
            foreach (var error in responses.Where(status => string.CompareOrdinal(status.Name, "400") >= 0))
            {
                await Assert.That(error.Value.GetProperty("content").GetProperty("application/json").TryGetProperty("example", out _)).IsTrue().Because($"{line} {error.Name}");
                var schema = error.Value.GetProperty("content").GetProperty("application/json").GetProperty("schema").GetRawText();
                await Assert.That(schema.Contains("ErrorBody", StringComparison.Ordinal) || schema.Contains("\"error\"", StringComparison.Ordinal)
                    || ((line.StartsWith("GET /readyz", StringComparison.Ordinal) || line.StartsWith("GET /backupz", StringComparison.Ordinal)
                        || line.StartsWith("GET /healthz", StringComparison.Ordinal)) && error.Name == "503" && schema.Contains("StatusBody", StringComparison.Ordinal))).IsTrue().Because($"{line} {error.Name}");
            }

            if (!line.Contains(" anonymous ", StringComparison.Ordinal))
            {
                await Assert.That(responses.Any(status => status.Name == "401")).IsTrue().Because(line);
                foreach (var scheme in operation.GetProperty("security").EnumerateArray().SelectMany(requirement => requirement.EnumerateObject()))
                {
                    await Assert.That(scheme.Value.GetArrayLength()).IsEqualTo(0).Because(line);
                }

                if (operation.TryGetProperty("x-required-claims", out var claims))
                {
                    await Assert.That(claims.EnumerateArray().Select(claim => claim.GetString()).Single()).IsEqualTo("platform_role=admin").Because(line);
                    await Assert.That(operation.GetProperty("description").GetString()).Contains("platform_role claim is admin").Because(line);
                    continue;
                }

                var scopes = operation.GetProperty("x-required-scopes").EnumerateArray().Select(scope => scope.GetString()).ToArray();
                await Assert.That(scopes.Length).IsGreaterThan(0).Because(line);
                await Assert.That(operation.GetProperty("description").GetString()).Contains($"Required JWT scopes: {string.Join(", ", scopes)}.").Because(line);
            }
        }
    }

    private static string Security(JsonElement operation) =>
        operation.TryGetProperty("security", out var security)
            ? string.Join(' ', security.EnumerateArray().SelectMany(requirement => requirement.EnumerateObject()).Select(scheme =>
                $"{scheme.Name}({string.Join(',', (operation.TryGetProperty("x-required-scopes", out var scopes) ? scopes : operation.GetProperty("x-required-claims")).EnumerateArray().Select(scope => scope.GetString()))})"))
            : "anonymous";

    private static Uri Uri(string path) => new(path, UriKind.Relative);
}

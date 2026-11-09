using System.Text.Json.Nodes;
using Microsoft.OpenApi;
using TerraBase.Hosting;

namespace TerraBase.Features.ApiReference;

/// <summary>Runnable request shapes and representative response envelopes for the public operations.</summary>
internal static class ApiExamples
{
    private const string Database = """
        {"id":"01ARZ3NDEKTSV4RRFFQ69G5FA1","projectId":"01ARZ3NDEKTSV4RRFFQ69G5FA2","name":"main","branchedFrom":null,"createdAt":"2026-10-07T12:00:00Z","region":"gru-1","homeRegion":"gru-1","replicaRegions":[],"allRegions":false,"expiresAt":null}
        """;
    private const string Expiring = """
        {"id":"01ARZ3NDEKTSV4RRFFQ69G5FA1","projectId":"01ARZ3NDEKTSV4RRFFQ69G5FA2","name":"run-42","branchedFrom":null,"createdAt":"2026-10-07T12:00:00Z","region":"gru-1","homeRegion":"gru-1","replicaRegions":[],"allRegions":false,"expiresAt":"2026-10-07T13:00:00Z"}
        """;
    private const string ReadGrant = """{"table":"notes","where":"owner_id = :sub","columns":["id","owner_id","body"],"updatedAt":"2026-11-04T12:00:00Z"}""";
    private const string DeclaredSchema = """{"$schema":"https://json-schema.org/draft/2020-12/schema","type":"object","required":["name"],"properties":{"name":{"type":"string"}}}""";
    private const string BucketRules = """{"owner":"/userId","read":{"owner":true,"public":false,"roles":[]},"write":{"owner":true,"public":false,"roles":[]}}""";
    private const string FileBucket = "{\"id\":\"01ARZ3NDEKTSV4RRFFQ69G5FAC\",\"name\":\"avatars\",\"region\":\"gru\",\"rules\":" + BucketRules + "}";
    private const string StoredFile = """{"id":"01ARZ3NDEKTSV4RRFFQ69G5FAD","name":"hello.txt","contentType":"text/plain","size":5,"sha256":"LPJNul+wow4m6DsqxbninhsWHlwfp0JecwQzYpOLmCQ=","metadata":{"userId":"01ARZ3NDEKTSV4RRFFQ69G5FAE"},"status":"ready","uploadExpiresAt":"2026-10-24T12:05:00Z","deleteAfter":null}""";
    private const string FileCapability = """{"id":"01ARZ3NDEKTSV4RRFFQ69G5FAD","url":"https://files.example/signed-capability","expiresAt":"2026-10-24T12:05:00Z","headers":{"Content-Type":"text/plain","Content-Length":"5","x-amz-checksum-sha256":"LPJNul+wow4m6DsqxbninhsWHlwfp0JecwQzYpOLmCQ="}}""";
    private const string Backup = """{"id":"01ARZ3NDEKTSV4RRFFQ69G5FA4","databaseId":"01ARZ3NDEKTSV4RRFFQ69G5FA1","status":"ready","trigger":"manual","sizeBytes":40960,"createdAt":"2026-10-07T12:00:00Z"}""";
    private const string Policy = """{"fullBackupCron":"0 2 * * *","retentionDays":7,"updatedAt":null}""";
    private const string PolicyStatus =
        """{"fullBackupCron":"0 2 * * *","retentionDays":7,"updatedAt":null,"lastSucceededAt":"2026-10-07T02:00:04Z","lastFailedAt":null,"lastError":null,"nextDueAt":"2026-10-08T02:00:00Z"}""";
    private const string Period = """{"from":"2026-10-07T00:00:00Z","to":"2026-10-08T00:00:00Z","granularity":"day","reads":0,"writes":0}""";
    private const string Series = """[{"start":"2026-10-07T00:00:00Z","reads":0,"writes":0}]""";
    private const string Function = """{"name":"hello","source":"function handler(event) { return event.body; }","cron":"*/5 * * * *","createdAt":"2026-10-07T12:00:00Z","updatedAt":"2026-10-07T12:00:00Z"}""";
    private const string Usage = """{"databaseId":"01ARZ3NDEKTSV4RRFFQ69G5FA1","storageBytes":40960,"logicalBytes":0,"reads":0,"writes":0,"period":PERIOD,"series":SERIES}""";

    public static void Apply(OpenApiOperation operation)
    {
        var id = operation.OperationId;
        ApplyRequestExamples(operation);

        if (operation.Responses is not { } responses)
        {
            return;
        }

        foreach (var (status, response) in responses)
        {
            if (response is not OpenApiResponse concrete)
            {
                continue;
            }

            var error = string.CompareOrdinal(status, "400") >= 0 && !(id == "readyz" && status == "503");
            var codes = Codes(id, status);
            if (error)
            {
                concrete.Description = $"{concrete.Description} Error codes: {codes}.";
            }

            foreach (var media in concrete.Content?.Values.OfType<OpenApiMediaType>() ?? [])
            {
                media.Example = error
                    ? new JsonObject { ["error"] = new JsonObject { ["code"] = codes.Split(", ", StringSplitOptions.None)[0], ["message"] = "The request cannot be served." } }
                    : JsonNode.Parse(Response(id, status));
            }
        }
    }

    public static void ApplyRequestExamples(OpenApiOperation operation)
    {
        if (operation.RequestBody?.Content is not { } request)
        {
            return;
        }

        foreach (var (mediaType, media) in request)
        {
            if (media is OpenApiMediaType body && body.Example is null)
            {
                body.Example = JsonNode.Parse(Request(operation.OperationId, mediaType));
            }
        }
    }

    private static string Request(string? id, string mediaType) => id switch
    {
        "createFileBucket" => "{\"name\":\"avatars\",\"rules\":" + BucketRules + "}",
        "setFileBucketRules" => "{\"rules\":" + BucketRules + "}",
        "createFileUpload" => """{"name":"hello.txt","contentType":"text/plain","size":5,"sha256":"LPJNul+wow4m6DsqxbninhsWHlwfp0JecwQzYpOLmCQ=","metadata":{"userId":"01ARZ3NDEKTSV4RRFFQ69G5FAE"}}""",
        "createDatabase" => """{"name":"run-42","ttlSeconds":3600}""",
        "setDatabaseTtl" => """{"ttlSeconds":3600}""",
        "putReadGrant" => """{"where":"owner_id = :sub","columns":["id","owner_id","body"]}""",
        "branchDatabase" or "branchDatabaseIfId" => """{"name":"main_branch","ttlSeconds":3600}""",
        "updateDatabase" => """{"replicaRegions":[]}""",
        "moveDatabaseHome" => """{"homeRegion":"sjc"}""",
        "restoreBackup" => """{"name":"restored"}""",
        "planPointInTime" => """{"pointInTime":"2026-10-07T12:00:13Z","destination":"platform"}""",
        "restorePointInTime" => """{"pointInTime":"2026-10-07T12:00:13Z","name":"restored","destination":"customer","ttlSeconds":3600}""",
        "rewindPointInTime" => """{"pointInTime":"2026-10-07T12:00:13Z","destination":"platform"}""",
        "setBackupDestination" => """{"endpoint":"https://s3.example.com","region":"us-east-1","bucket":"customer-backups","accessKeyId":"YOUR_ACCESS_KEY","secretKey":"YOUR_SECRET_KEY","mode":"both"}""",
        "setBackupPolicy" => """{"fullBackupCron":"0 2 * * *","retentionDays":7}""",
        "deployFunction" => """{"source":"function handler(event) { return event.body; }","cron":null}""",
        "invokeFunction" => """{"name":"Ada"}""",
        "executeSql" => """{"statements":[{"sql":"CREATE TABLE IF NOT EXISTS people (id INTEGER PRIMARY KEY, name TEXT NOT NULL)"}]}""",
        _ => "{}",
    };

    private static string Response(string? id, string status) => id switch
    {
        "createFileBucket" or "setFileBucketRules" => FileBucket,
        "listFileBuckets" => "{\"items\":[" + FileBucket + "],\"next\":null}",
        "listBucketFiles" => "{\"items\":[" + StoredFile + "],\"next\":null}",
        "completeFileUpload" => StoredFile,
        "createFileUpload" => FileCapability,
        "createFileDownload" => """{"id":"01ARZ3NDEKTSV4RRFFQ69G5FAD","url":"https://files.example/signed-capability","expiresAt":"2026-10-24T12:01:00Z","headers":{}}""",
        "healthz" => $$"""{"status":"{{(status == "503" ? "degraded" : "ok")}}","version":"{{ServiceVersion.Value}}","region":"gru-1"}""",
        "readyz" => $$"""{"status":"{{(status == "503" ? "unavailable" : "ready")}}","version":"{{ServiceVersion.Value}}","region":"gru-1"}""",
        "createDatabase" or "setDatabaseTtl" => Expiring,
        "getDatabase" or "updateDatabase" or "clearDatabaseTtl" or "moveDatabaseHome" => Database,
        "putReadGrant" or "getReadGrant" => ReadGrant,
        "listReadGrants" => "{\"grants\":[" + ReadGrant + "]}",
        "branchDatabase" or "branchDatabaseIfId" => """{"id":"01ARZ3NDEKTSV4RRFFQ69G5FA6","projectId":"01ARZ3NDEKTSV4RRFFQ69G5FA2","name":"main_branch","branchedFrom":"01ARZ3NDEKTSV4RRFFQ69G5FA1","createdAt":"2026-10-07T12:00:00Z","region":"gru-1","homeRegion":"gru-1","replicaRegions":[],"allRegions":false,"expiresAt":"2026-10-07T13:00:00Z"}""",
        "listDatabases" => Page(Database),
        "createBackup" => Backup,
        "listBackups" => Page(Backup),
        "restoreBackup" => """{"id":"01ARZ3NDEKTSV4RRFFQ69G5FA7","operationId":"01ARZ3NDEKTSV4RRFFQ69G5FA7","status":"running","kind":"backup","database":"restored","errorCode":null,"errorMessage":null,"createdAt":"2026-10-07T12:00:00Z","finishedAt":null}""",
        "getRestoreOperation" => """{"id":"01ARZ3NDEKTSV4RRFFQ69G5FA7","operationId":"01ARZ3NDEKTSV4RRFFQ69G5FA7","status":"completed","kind":"backup","database":"restored","errorCode":null,"errorMessage":null,"createdAt":"2026-10-07T12:00:00Z","finishedAt":"2026-10-07T12:00:05Z"}""",
        "getBackupPolicy" => PolicyStatus,
        "getBackupDestination" or "setBackupDestination" => """{"endpoint":"https://s3.example.com","region":"us-east-1","bucket":"customer-backups","mode":"both","updatedAt":"2026-10-07T12:00:00Z"}""",
        "getRestoreWindow" => """{"earliest":"2026-10-07T12:00:00Z","latest":"2026-10-07T12:00:16Z"}""",
        "planPointInTime" => """{"window":{"earliest":"2026-10-07T12:00:00Z","latest":"2026-10-07T12:00:16Z"},"pointInTime":"2026-10-07T12:00:13Z","snapshotAt":"2026-10-07T12:00:10Z","latestSegmentAt":null,"segmentCount":0}""",
        "setBackupPolicy" => Policy.Replace("\"updatedAt\":null", "\"updatedAt\":\"2026-10-07T12:00:00Z\"", StringComparison.Ordinal),
        "getDatabaseUsage" => Usage.Replace("PERIOD", Period, StringComparison.Ordinal).Replace("SERIES", Series, StringComparison.Ordinal),
        "getProjectUsage" => "{\"projectId\":\"01ARZ3NDEKTSV4RRFFQ69G5FA2\",\"databases\":0,\"storageBytes\":0,\"logicalBytes\":0,\"reads\":0,\"writes\":0,\"period\":" + Period + ",\"series\":" + Series + ",\"items\":[]}",
        "deployFunction" or "getFunction" => Function,
        "listFunctions" => "[" + Function.Replace("\"source\":\"function handler(event) { return event.body; }\"", "\"source\":null", StringComparison.Ordinal) + "]",
        "listFunctionLogs" => """[{"id":"01ARZ3NDEKTSV4RRFFQ69G5FAC","at":"2026-10-07T12:00:00Z","trigger":"http","status":"ok","durationMs":3,"lines":["hello Ada"],"error":null}]""",
        "invokeFunction" => """{"hello":"Ada"}""",

        "executeSql" => """{"results":[{"columns":[],"rows":[],"rows_affected":0,"last_insert_rowid":null}]}""",
        _ => "{}",
    };

    private static string Codes(string? id, string status) => status switch
    {
        "400" when id is "getDatabaseUsage" or "getProjectUsage" =>
            "invalid_request, invalid_query",
        "400" when id == "connectSql" => "invalid_request, websocket_required",
        "400" when id == "executeSql" => "syntax_error, undefined_table, undefined_column, duplicate_object, primary_key_violation, unique_violation, not_null_violation, check_violation, foreign_key_violation, constraint_violation, query_canceled, read_only_transaction, transaction_state, sql_error, invalid_request",
        "400" when id is "putReadGrant" => "invalid_request, unknown_column, unsafe_where",
        "404" when id is "putReadGrant" => "unknown_database, unknown_table",
        "404" when id is "getReadGrant" or "deleteReadGrant" => "unknown_database, no_read_grant",
        "400" => "invalid_request",
        "401" => "invalid_key",
        "403" when id is "moveDatabaseHome" or "createDatabase" or "setDatabaseTtl" or "clearDatabaseTtl" or "branchDatabase" or "branchDatabaseIfId" or "updateDatabase" or "deleteDatabase" or "deleteDatabaseIfId" or "restoreBackup" or "setBackupPolicy" => "forbidden_scope, forbidden_role",
        "403" when id == "executeSql" => "forbidden_scope, sql_sandbox",
        "403" => "forbidden_scope",
        "404" when id is "moveDatabaseHome" or "createDatabase" or "setDatabaseTtl" or "clearDatabaseTtl" or "getDatabase" or "listDatabases" or "updateDatabase" or "deleteDatabase" or "deleteDatabaseIfId" or "branchDatabase" or "branchDatabaseIfId" or "getDatabaseUsage" or "getProjectUsage" => "unknown_database, unknown_route",
        "404" => "unknown_database, unknown_route, not_found",
        "408" => "request_timeout",
        "409" when id == "deleteDatabase" => "database_replicated, wrong_region, move_in_progress",
        "409" when id == "moveDatabaseHome" => "move_in_progress",
        "409" when id == "deleteDatabaseIfId" => "database_identity_mismatch, wrong_region, move_in_progress",
        "409" when id == "branchDatabaseIfId" => "database_identity_changed, name_taken",
        "409" when id is "createDatabase" or "branchDatabase" or "restoreBackup" or "restorePointInTime" => "name_taken",
        "409" when id == "executeSql" => "serialization_conflict",
        "409" => "conflict",
        "412" => "precondition_failed",
        "413" when id == "executeSql" => "too_large, payload_too_large",
        "413" => "payload_too_large",
        "415" => "unsupported_media_type",
        "421" => "region_pin_mismatch, invalid_region_pin, misdirected_request, wrong_region",
        "429" when id == "executeSql" => "quota_exceeded, tenant_busy, tenant_memory",
        "429" => "tenant_busy, tenant_memory",
        "503" when id == "moveDatabaseHome" => "replica_not_ready, move_unresolved, storage_busy",
        "503" when id == "planPointInTime" => "pitr_plan_limit, pitr_plan_timeout, pitr_plan_unavailable, storage_busy",
        "503" when id is "getRestoreWindow" or "restorePointInTime" or "rewindPointInTime" => "pitr_plan_limit, storage_busy, capacity, unavailable",
        "503" when id == "healthz" => "bucket_missing",
        "503" => "storage_busy, capacity, unavailable",
        _ => "internal_error",
    };

    private static string Page(string item) => "{\"items\":[" + item + "],\"next\":null}";
}

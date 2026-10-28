using System.Text.Json.Nodes;
using Microsoft.OpenApi;
using TerraBase.Hosting;

namespace TerraBase.Features.ApiReference;

/// <summary>Runnable request shapes and representative response envelopes for the public operations.</summary>
internal static class ApiExamples
{
    private const string Database = """
        {"id":"01ARZ3NDEKTSV4RRFFQ69G5FA1","projectId":"01ARZ3NDEKTSV4RRFFQ69G5FA2","name":"main","branchedFrom":null,"createdAt":"2026-10-07T12:00:00Z","region":"gru-1","homeRegion":"gru-1","replicaRegions":[],"allRegions":false}
        """;
    private const string DeclaredSchema = """{"$schema":"https://json-schema.org/draft/2020-12/schema","type":"object","required":["name"],"properties":{"name":{"type":"string"}}}""";
    private const string CollectionRules = """{"owner":"/userId","read":{"owner":true,"public":false,"roles":[]},"write":{"owner":true,"public":false,"roles":[]}}""";
    private const string FileBucket = "{\"id\":\"01ARZ3NDEKTSV4RRFFQ69G5FAC\",\"name\":\"avatars\",\"region\":\"gru\",\"rules\":" + CollectionRules + "}";
    private const string StoredFile = """{"id":"01ARZ3NDEKTSV4RRFFQ69G5FAD","name":"hello.txt","contentType":"text/plain","size":5,"sha256":"LPJNul+wow4m6DsqxbninhsWHlwfp0JecwQzYpOLmCQ=","metadata":{"userId":"01ARZ3NDEKTSV4RRFFQ69G5FAE"},"status":"ready","uploadExpiresAt":"2026-10-24T12:05:00Z","deleteAfter":null}""";
    private const string FileCapability = """{"id":"01ARZ3NDEKTSV4RRFFQ69G5FAD","url":"https://files.example/signed-capability","expiresAt":"2026-10-24T12:05:00Z","headers":{"Content-Type":"text/plain","Content-Length":"5","x-amz-checksum-sha256":"LPJNul+wow4m6DsqxbninhsWHlwfp0JecwQzYpOLmCQ="}}""";
    private const string Collection = """{"id":"01ARZ3NDEKTSV4RRFFQ69G5FA7","name":"people","created_at":"1791374400000"}""";
    private const string Document = """{"id":"01ARZ3NDEKTSV4RRFFQ69G5FA3","version":"opaque-version-from-server","state":"value","document":{"name":"Ada"}}""";
    private const string Write = """{"id":"01ARZ3NDEKTSV4RRFFQ69G5FA3","version":"opaque-version-from-server","offset":"1","durability":"local_durable"}""";
    private const string Index = """{"name":"people_name","collection":"people","fields":["name"],"state":"active"}""";
    private const string Backup = """{"id":"01ARZ3NDEKTSV4RRFFQ69G5FA4","databaseId":"01ARZ3NDEKTSV4RRFFQ69G5FA1","status":"ready","trigger":"manual","sizeBytes":40960,"createdAt":"2026-10-07T12:00:00Z"}""";
    private const string Policy = """{"fullBackupCron":"0 2 * * *","retentionDays":7,"disabled":false,"updatedAt":null}""";
    private const string Saved = """{"id":"01ARZ3NDEKTSV4RRFFQ69G5FA5","name":"People","sql":"SELECT * FROM people LIMIT 50","createdAt":"2026-10-07T12:00:00Z","updatedAt":null}""";
    private const string Period = """{"from":"2026-10-07T00:00:00Z","to":"2026-10-08T00:00:00Z","granularity":"day","reads":0,"writes":0}""";
    private const string Series = """[{"start":"2026-10-07T00:00:00Z","reads":0,"writes":0}]""";
    private const string ImportJob = """
        {"id":"01ARZ3NDEKTSV4RRFFQ69G5FA8","project":"01ARZ3NDEKTSV4RRFFQ69G5FA2","database":"import-01arz3ndektsv4rrffq69g5fa9","region":"gru-1","state":"ready","cancelRequested":false,"options":{"exclude":[]},"source":{"format":"postgres_plain","bytes":8165,"sha256":"9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08","receivedBytes":8165,"staged":true,"retainedUntil":null},"preview":{"id":"01ARZ3NDEKTSV4RRFFQ69G5FAA","tables":[{"source":"public.orgs","collection":"orgs","excluded":false,"rows":3,"primaryKey":["id"],"columns":[{"name":"id","sourceType":"bigint","mapping":"integer","nullable":false,"excluded":false},{"name":"name","sourceType":"text","mapping":"string","nullable":false,"excluded":false}],"constraints":[{"kind":"primary_key","name":"orgs_pkey","columns":["id"],"references":null,"enforced":false}],"issues":[]}],"skipped":[{"kind":"index","name":"public.users_org_idx"}],"skippedTruncated":false,"blocking":0,"warnings":0},"confirmedPreviewId":null,"progress":null,"validation":null,"error":null,"createdAt":"2026-10-12T12:00:00+00:00","updatedAt":"2026-10-12T12:00:05+00:00","startedAt":null,"finishedAt":null}
        """;
    private const string Function = """{"name":"hello","source":"function handler(event) { return event.body; }","trigger":null,"cron":"*/5 * * * *","createdAt":"2026-10-07T12:00:00Z","updatedAt":"2026-10-07T12:00:00Z"}""";
    private const string Usage = """{"databaseId":"01ARZ3NDEKTSV4RRFFQ69G5FA1","storageBytes":40960,"logicalBytes":0,"collections":0,"documents":0,"reads":0,"writes":0,"period":PERIOD,"series":SERIES}""";

    public static void Apply(OpenApiOperation operation)
    {
        var id = operation.OperationId;
        if (id is "getDocument" or "putDocument" or "patchDocument" or "deleteDocument")
        {
            operation.Parameters ??= [];
            operation.Parameters.Add(new OpenApiParameter
            {
                Name = "id",
                In = ParameterLocation.Query,
                Required = true,
                Description = "The document's canonical ULID (26 uppercase Crockford base32 characters), supplied exactly once; any other form answers 400 invalid_document_id. No other query parameters are accepted.",
                Schema = new OpenApiSchema { Type = JsonSchemaType.String, MinLength = 26, MaxLength = 26, Pattern = "^[0-7][0-9A-HJKMNP-TV-Z]{25}$" },
                Example = JsonValue.Create("01ARZ3NDEKTSV4RRFFQ69G5FAV"),
            });
        }

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
        "createFileBucket" => "{\"name\":\"avatars\",\"rules\":" + CollectionRules + "}",
        "setFileBucketRules" => "{\"rules\":" + CollectionRules + "}",
        "createFileUpload" => """{"name":"hello.txt","contentType":"text/plain","size":5,"sha256":"LPJNul+wow4m6DsqxbninhsWHlwfp0JecwQzYpOLmCQ=","metadata":{"userId":"01ARZ3NDEKTSV4RRFFQ69G5FAE"}}""",
        "createDatabase" => """{"name":"main"}""",
        "branchDatabase" or "branchDatabaseIfId" => """{"name":"main_branch"}""",
        "updateDatabase" => """{"replicaRegions":[]}""",
        "restoreBackup" => """{"name":"restored"}""",
        "planPointInTime" => """{"pointInTime":"2026-10-07T12:00:13Z","destination":"platform"}""",
        "previewSelectiveRecovery" => """{"pointInTime":"2026-10-07T12:00:13Z","selections":[{"collection":"people","ids":["01ARZ3NDEKTSV4RRFFQ69G5FA3"]}]}""",
        "recoverHistoricalRecords" => """{"approval":"opaque-approval-from-preview","records":[{"collection":"people","id":"01ARZ3NDEKTSV4RRFFQ69G5FA3"}]}""",
        "restorePointInTime" => """{"pointInTime":"2026-10-07T12:00:13Z","name":"restored","destination":"customer"}""",
        "setBackupDestination" => """{"endpoint":"https://s3.example.com","region":"us-east-1","bucket":"customer-backups","accessKeyId":"YOUR_ACCESS_KEY","secretKey":"YOUR_SECRET_KEY","mode":"both"}""",
        "setBackupPolicy" => """{"fullBackupCron":"0 2 * * *","retentionDays":7,"disabled":false}""",
        "setExportSchedule" => """{"cron":"0 3 * * *"}""",
        "createIndex" => """{"collection":"people","fields":["name"],"name":"people_name"}""",
        "setAutoIndexPolicy" => """{"enabled":false}""",
        "createCollection" => """{"name":"people"}""",
        "createCollectionWebhook" => """{"url":"https://receiver.example/events"}""",
        "deployFunction" => """{"source":"function handler(event) { for (const c of event.changes) db.put(\"audit\", c.id, { source: c.id }); }","trigger":{"collection":"orders","events":["upsert"]},"cron":null}""",
        "invokeFunction" => """{"name":"Ada"}""",
        "putDeclaredCollectionSchema" or "previewCollectionSchema" => DeclaredSchema,
        "putCollectionRules" => CollectionRules,
        "putCollectionTtl" => """{"path":"/expiresAt"}""",
        "declareCollectionSchema" => """{"fields":[{"name":"email","unique":true,"normalization":"lowercase"}],"indexes":[{"name":"tenant_handle","fields":["tenant","handle"],"unique":true}]}""",
        "createDocument" or "putDocument" => """{"name":"Ada"}""",
        "patchDocument" when mediaType == "application/vnd.terrabase.ops+json" =>
            """{"ops":[{"op":"set","path":"/name","value":"Ada"},{"op":"inc","path":"/visits","by":1},{"op":"add","path":"/roles","value":"admin"}]}""",
        "patchDocument" => """{"name":"Ada"}""",
        "queryDocuments" => """{"filter":{"eq":["/name","Ada"]},"limit":50,"count":true}""",
        "explainSql" => """{"sql":"SELECT * FROM people WHERE email = 'person@example.com' LIMIT 10"}""",
        "runSql" or "checkSql" => """{"sql":"SELECT * FROM people LIMIT 50"}""",
        "createSavedQuery" or "updateSavedQuery" => """{"name":"People","sql":"SELECT * FROM people LIMIT 50"}""",
        "runTransaction" => """{"ops":[{"op":"create","collection":"people","document":{"name":"Ada"}}]}""",
        "executeSql" => """{"statements":[{"sql":"CREATE TABLE IF NOT EXISTS people (id INTEGER PRIMARY KEY, name TEXT NOT NULL)"}]}""",
        "createImport" => """{"format":"postgres_plain","sourceBytes":8165,"sourceSha256":"9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08","options":{"exclude":["public.audit_log"]}}""",
        "updateImport" => """{"options":{"exclude":["public.tagged.tags"]}}""",
        "startImport" => """{"previewId":"01ARZ3NDEKTSV4RRFFQ69G5FAA"}""",
        "uploadImportSource" => "\"<up to 4 MiB of the source file, starting at offset>\"",
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
        "healthz" => $$"""{"status":"ok","version":"{{ServiceVersion.Value}}","region":"gru-1"}""",
        "readyz" => $$"""{"status":"{{(status == "503" ? "unavailable" : "ready")}}","version":"{{ServiceVersion.Value}}","region":"gru-1"}""",
        "createDatabase" or "getDatabase" or "updateDatabase" => Database,
        "branchDatabase" or "branchDatabaseIfId" => """{"id":"01ARZ3NDEKTSV4RRFFQ69G5FA6","projectId":"01ARZ3NDEKTSV4RRFFQ69G5FA2","name":"main_branch","branchedFrom":"01ARZ3NDEKTSV4RRFFQ69G5FA1","createdAt":"2026-10-07T12:00:00Z","region":"gru-1","homeRegion":"gru-1","replicaRegions":[],"allRegions":false}""",
        "listDatabases" => Page(Database),
        "createBackup" => Backup,
        "listBackups" => Page(Backup),
        "restoreBackup" => """{"id":"01ARZ3NDEKTSV4RRFFQ69G5FA6","projectId":"01ARZ3NDEKTSV4RRFFQ69G5FA2","name":"restored","branchedFrom":"01ARZ3NDEKTSV4RRFFQ69G5FA1","createdAt":"2026-10-07T12:00:00Z","homeRegion":"gru-1","replicaRegions":[],"placementVersion":0,"allRegions":false}""",
        "getBackupPolicy" => Policy,
        "getExportSchedule" or "setExportSchedule" => """{"cron":"0 3 * * *","updatedAt":"2026-10-07T12:00:00Z","lastRun":{"startedAt":"2026-10-08T03:00:00Z","finishedAt":"2026-10-08T03:00:04Z","status":"succeeded","key":"terrabase-exports/01ARZ3NDEKTSV4RRFFQ69G5FA2/01ARZ3NDEKTSV4RRFFQ69G5FA1/20261008T030000Z.ndjson","documents":1200,"bytes":184320,"error":null}}""",
        "getBackupDestination" or "setBackupDestination" => """{"endpoint":"https://s3.example.com","region":"us-east-1","bucket":"customer-backups","mode":"both","updatedAt":"2026-10-07T12:00:00Z"}""",
        "getRestoreWindow" => """{"earliest":"2026-10-07T12:00:00Z","latest":"2026-10-07T12:00:16Z"}""",
        "previewSelectiveRecovery" => """{"approval":"opaque-approval-from-preview","expiresAt":"2026-10-07T12:10:00Z","records":[{"collection":"people","id":"01ARZ3NDEKTSV4RRFFQ69G5FA3","historical":{"name":"Ada"},"current":null,"currentVersion":"opaque-tombstone-version"}]}""",
        "planPointInTime" => """{"window":{"earliest":"2026-10-07T12:00:00Z","latest":"2026-10-07T12:00:16Z"},"pointInTime":"2026-10-07T12:00:13Z","snapshotAt":"2026-10-07T12:00:10Z","latestSegmentAt":null,"segmentCount":0}""",
        "setBackupPolicy" => Policy.Replace("\"updatedAt\":null", "\"updatedAt\":\"2026-10-07T12:00:00Z\"", StringComparison.Ordinal),
        "createIndex" => Index,
        "listIndexes" => Items(Index),
        "getAutoIndexPolicy" or "setAutoIndexPolicy" => """{"collection":"people","enabled":false}""",
        "listIndexRecommendations" => Items("""{"id":"people:name","collection":"people","fields":["name"],"reason":"1 queries filtered or sorted on this field without an index","queries":1}"""),
        "listQueryShapes" => """{"items":[]}""",
        "applyIndexRecommendation" => "{\"status\":\"" + (status == "200" ? "already-applied" : "applied") + "\",\"index\":" + Index + "}",
        "getDatabaseSchema" => Items("""{"name":"people","documents":1,"bytes":14}"""),
        "getCollectionSchema" => """{"collection":"people","documents":1,"sampledDocuments":1,"fields":[{"path":"name","present":1,"types":{"string":1}}],"uniqueConstraints":[]}""",
        "declareCollectionSchema" => """{"constraints":[{"name":"field_82244417f956ac7c","fields":["email"],"normalization":"lowercase"},{"name":"tenant_handle","fields":["tenant","handle"],"normalization":"none"}]}""",
        "getDatabaseUsage" => Usage.Replace("PERIOD", Period, StringComparison.Ordinal).Replace("SERIES", Series, StringComparison.Ordinal),
        "getProjectUsage" => "{\"projectId\":\"01ARZ3NDEKTSV4RRFFQ69G5FA2\",\"databases\":0,\"collections\":0,\"storageBytes\":0,\"logicalBytes\":0,\"documents\":0,\"reads\":0,\"writes\":0,\"period\":" + Period + ",\"series\":" + Series + ",\"items\":[]}",
        "createCollection" or "getCollection" or "dropCollection" => Collection,
        "createCollectionWebhook" => """{"id":"01ARZ3NDEKTSV4RRFFQ69G5FAA","url":"https://receiver.example/events","createdAt":"2026-10-07T12:00:00Z","status":"active","secret":"<creation-only base64url key>"}""",
        "deployFunction" or "getFunction" => Function,
        "listFunctions" => "[" + Function.Replace("\"source\":\"function handler(event) { return event.body; }\"", "\"source\":null", StringComparison.Ordinal) + "]",
        "listFunctionLogs" => """[{"id":"01ARZ3NDEKTSV4RRFFQ69G5FAC","at":"2026-10-07T12:00:00Z","trigger":"http","status":"ok","durationMs":3,"lines":["hello Ada"],"error":null}]""",
        "invokeFunction" => """{"hello":"Ada"}""",
        "listFunctionDeliveries" => """[{"id":"01ARZ3NDEKTSV4RRFFQ69G5FAB","createdAt":"2026-10-07T12:00:00Z","status":"pending","attempts":2,"lastAttemptAt":"2026-10-07T12:00:01Z","nextAttemptAt":"2026-10-07T12:00:03Z","httpStatus":500,"errorCode":"function_error"}]""",
        "listCollectionWebhooks" => """[{"id":"01ARZ3NDEKTSV4RRFFQ69G5FAA","url":"https://receiver.example/events","createdAt":"2026-10-07T12:00:00Z","status":"active"}]""",
        "listCollectionWebhookDeliveries" => """[{"id":"01ARZ3NDEKTSV4RRFFQ69G5FAB","createdAt":"2026-10-07T12:00:00Z","status":"pending","attempts":1,"lastAttemptAt":"2026-10-07T12:00:00Z","nextAttemptAt":"2026-10-07T12:00:01Z","httpStatus":503,"errorCode":"http_error"}]""",

        "getCollectionRules" or "putCollectionRules" => "{\"rules\":" + CollectionRules + "}",
        "getDeclaredCollectionSchema" or "putDeclaredCollectionSchema" => "{\"schema\":" + DeclaredSchema + ",\"dialect\":\"https://json-schema.org/draft/2020-12/schema\"}",
        "previewCollectionSchema" => """{"schema_hash":"78f9197ddd038592a802490c0febed471e8f3a0a8de4504c1844d0f24bae2b0d","frontier":"0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef","examined":2,"valid":2,"invalid":0,"complete":true,"next_cursor":null,"diagnostics":[]}""",
        "deleteDeclaredCollectionSchema" => """{"schema":null}""",
        "getCollectionTtl" or "putCollectionTtl" => """{"path":"/expiresAt"}""",
        "deleteCollectionTtl" => """{"path":null}""",
        "listCollections" => DataPage(Collection),
        "getDocument" => Document,
        "createDocument" or "putDocument" or "deleteDocument" => Write,
        "patchDocument" => Write[..^1] + ""","document":{"name":"Ada","roles":["admin"],"visits":1}}""",
        "listDocuments" => DataPage(Document),
        "queryDocuments" => "{\"items\":[" + Document + "],\"next_cursor\":null,\"count\":{\"value\":\"1\",\"exact\":true}}",
        "runSql" => """{"matched":1,"truncated":false,"rows":[{"id":"01ARZ3NDEKTSV4RRFFQ69G5FA3","value":{"name":"Ada"}}]}""",
        "explainSql" => """{"ok":true,"error":null,"collection":"people","hasLimit":true,"plan":["SEARCH people USING INDEX tb_index_people_email (<expr>=?)"],"indexes":["people_email"],"suggestions":[]}""",
        "checkSql" => """{"ok":true,"error":null,"collection":"people","hasLimit":true,"plan":[],"indexes":[],"suggestions":[]}""",
        "createSavedQuery" or "getSavedQuery" => Saved,
        "updateSavedQuery" => Saved.Replace("\"updatedAt\":null", "\"updatedAt\":\"2026-10-07T12:00:00Z\"", StringComparison.Ordinal),
        "listSavedQueries" => Page(Saved),
        "executeSql" => """{"results":[{"columns":[],"rows":[],"rows_affected":0,"last_insert_rowid":null}]}""",
        "runTransaction" or "recoverHistoricalRecords" => """{"offset":"1","durability":"local_durable","results":[{"id":"01ARZ3NDEKTSV4RRFFQ69G5FA3","version":"opaque-version-from-server"}]}""",
        "createImport" or "getImport" or "uploadImportSource" or "updateImport" or "startImport" or "cancelImport" or "resumeImport" => ImportJob,
        _ => "{}",
    };

    private static string Codes(string? id, string status) => status switch
    {
        "400" when id == "queryDocuments" => "invalid_request, invalid_query, invalid_cursor, sort_requires_index",
        "400" when id is "getDatabaseSchema" or "getCollectionSchema" or "runSql" or "checkSql" or "explainSql" or "getDatabaseUsage" or "getProjectUsage" =>
            "invalid_request, invalid_query",
        "400" when id == "putDeclaredCollectionSchema" => "invalid_request, invalid_schema",
        "400" when id == "previewCollectionSchema" => "invalid_request, invalid_schema, invalid_cursor",
        "400" when id == "putCollectionRules" => "invalid_request, invalid_rules",
        "400" when id == "putCollectionTtl" => "invalid_request, invalid_ttl",
        "400" when id is "createDocument" or "putDocument" => "invalid_request, schema_validation_failed",
        "400" when id == "patchDocument" => "invalid_request, invalid_patch, nested_lattice_path, schema_validation_failed",
        "400" when id == "setAutoIndexPolicy" => "invalid_request, auto_index_policy_limit",
        "400" when id == "listCollections" => "invalid_request, invalid_cursor",
        "400" when id == "listDocuments" => "invalid_request, invalid_cursor, invalid_query",
        "400" when id == "runTransaction" => "invalid_request, invalid_patch, nested_lattice_path, invalid_query, assert_requires_index, schema_validation_failed",
        "400" when id == "executeSql" => "sql_error, invalid_request",
        "400" => "invalid_request",
        "401" => "invalid_key",
        "403" when id is "createDatabase" or "branchDatabase" or "branchDatabaseIfId" or "updateDatabase" or "deleteDatabase" or "deleteDatabaseIfId" or "restoreBackup" or "setBackupPolicy" or "setExportSchedule" or "setAutoIndexPolicy" or "createIndex" or "dropIndex" or "applyIndexRecommendation" or "createSavedQuery" or "updateSavedQuery" or "deleteSavedQuery" or "declareCollectionSchema" => "forbidden_scope, forbidden_role",
        "403" when id == "previewCollectionSchema" => "forbidden_scope, forbidden",
        "403" => "forbidden_scope",
        "404" when id is "createCollection" or "listCollections" => "unknown_database",
        "404" when id is "getDocument" or "patchDocument" or "deleteDocument" or "runTransaction" => "unknown_database, unknown_collection, not_found",
        "404" when id is "getDeclaredCollectionSchema" or "putDeclaredCollectionSchema" or "deleteDeclaredCollectionSchema" or "previewCollectionSchema"
            or "getCollectionTtl" or "putCollectionTtl" or "deleteCollectionTtl"
            or "getCollectionRules" or "putCollectionRules" => "unknown_database, unknown_collection",
        "404" when id is "getCollection" or "dropCollection" or "listDocuments" or "createDocument" or "putDocument" or "queryDocuments" => "unknown_database, unknown_collection",
        "404" when id is "createDatabase" or "getDatabase" or "listDatabases" or "updateDatabase" or "deleteDatabase" or "deleteDatabaseIfId" or "branchDatabase" or "branchDatabaseIfId" or "getDatabaseUsage" or "getProjectUsage" => "unknown_database, unknown_route",
        "404" when id == "createImport" => "unknown_database",
        "404" when id is "getImport" or "uploadImportSource" or "updateImport" or "startImport" or "cancelImport" or "resumeImport" => "unknown_database, unknown_import",
        "404" => "unknown_database, unknown_route, not_found",
        "408" => "request_timeout",
        "409" when id is "patchDocument" or "runTransaction" => "patch_conflict, unique_violation",
        "409" when id is "createDocument" or "putDocument" or "getDocument" => "unique_violation",
        "409" when id == "declareCollectionSchema" => "schema_requires_empty_collection",
        "409" when id == "previewCollectionSchema" => "preview_stale",
        "409" when id == "deleteDatabase" => "database_replicated, wrong_region",
        "409" when id == "deleteDatabaseIfId" => "database_identity_mismatch, wrong_region",
        "409" when id == "branchDatabaseIfId" => "database_identity_changed, name_taken",
        "409" when id == "createIndex" => "index_exists",
        "409" when id == "setExportSchedule" => "conflict",
        "409" when id is "createDatabase" or "branchDatabase" or "restoreBackup" or "restorePointInTime" or "createCollection" or "applyIndexRecommendation" => "name_taken",
        "409" when id == "createImport" => "destination_not_empty, import_exists",
        "409" when id == "uploadImportSource" => "invalid_state, offset_mismatch",
        "409" when id == "startImport" => "invalid_state, preview_changed, preview_blocked",
        "409" when id == "resumeImport" => "invalid_state, source_required",
        "409" when id is "updateImport" or "cancelImport" => "invalid_state",
        "409" when id == "executeSql" => "sql_conflict",
        "409" when id is "previewSelectiveRecovery" or "recoverHistoricalRecords" => "recovery_erasure_fenced, recovery_preview_invalid, recovery_history_changed, backup_history_incomplete, restore_point_unproven, backup_unreadable, unique_conflict, unique_violation",
        "409" => "conflict",
        "412" => "precondition_failed",
        "413" when id == "executeSql" => "sql_too_large, payload_too_large",
        "413" => "payload_too_large",
        "415" => "unsupported_media_type",
        "421" => "misdirected_request, wrong_region",
        "429" when id == "recoverHistoricalRecords" => "storage_quota, tenant_busy, tenant_memory",
        "429" => "tenant_busy, tenant_memory",
        "503" when id is "dropCollection" or "runTransaction" => "capacity, storage_busy, unavailable",
        "503" when id == "planPointInTime" => "pitr_plan_limit, pitr_plan_timeout, pitr_plan_unavailable, storage_busy",
        "503" when id is "createDocument" or "putDocument" or "patchDocument" or "deleteDocument" => "capacity, storage_busy, unavailable",
        "503" when id is "queryDocuments" or "getDocument" or "listDocuments" or "createCollection" or "getCollection" or "listCollections" => "storage_busy, unavailable",
        "503" when id is "createImport" or "getImport" or "uploadImportSource" or "updateImport" or "startImport" or "cancelImport" or "resumeImport" =>
            "capacity, unavailable, destination_modified",
        "503" when id is "previewSelectiveRecovery" or "recoverHistoricalRecords" => "capacity, disk_capacity, backup_inventory_limit, recovery_unavailable, storage_busy, unavailable",
        "503" => "storage_busy, capacity, unavailable",
        _ => "internal_error",
    };

    private static string Page(string item) => "{\"items\":[" + item + "],\"next\":null}";

    private static string DataPage(string item) => "{\"items\":[" + item + "],\"next_cursor\":null}";

    private static string Items(string item) => "{\"items\":[" + item + "]}";
}

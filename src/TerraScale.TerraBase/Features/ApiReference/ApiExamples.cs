using System.Text.Json.Nodes;
using Microsoft.OpenApi;
using TerraBase.Hosting;

namespace TerraBase.Features.ApiReference;

/// <summary>Runnable request shapes and representative response envelopes for the public operations.</summary>
internal static class ApiExamples
{
    private const string Database = """
        {"id":"01ARZ3NDEKTSV4RRFFQ69G5FA1","projectId":"01ARZ3NDEKTSV4RRFFQ69G5FA2","name":"main","branchedFrom":null,"createdAt":"2026-10-07T12:00:00Z","region":"gru-1","homeRegion":"gru-1","replicaRegions":[]}
        """;
    private const string DeclaredSchema = """{"$schema":"https://json-schema.org/draft/2020-12/schema","type":"object","required":["name"],"properties":{"name":{"type":"string"}}}""";
    private const string Collection = """{"id":"01ARZ3NDEKTSV4RRFFQ69G5FA7","name":"people","created_at":"1791374400000"}""";
    private const string Document = """{"id":"01ARZ3NDEKTSV4RRFFQ69G5FA3","version":"opaque-version-from-server","state":"value","document":{"name":"Ada"}}""";
    private const string Write = """{"id":"01ARZ3NDEKTSV4RRFFQ69G5FA3","version":"opaque-version-from-server","offset":"1","durability":"local_durable"}""";
    private const string Index = """{"name":"people_name","collection":"people","fields":["name"],"state":"active"}""";
    private const string Backup = """{"id":"01ARZ3NDEKTSV4RRFFQ69G5FA4","databaseId":"01ARZ3NDEKTSV4RRFFQ69G5FA1","status":"ready","trigger":"manual","sizeBytes":40960,"createdAt":"2026-10-07T12:00:00Z"}""";
    private const string Policy = """{"fullBackupCron":"0 2 * * *","retentionDays":7,"disabled":false,"updatedAt":null}""";
    private const string Saved = """{"id":"01ARZ3NDEKTSV4RRFFQ69G5FA5","name":"People","sql":"SELECT * FROM people LIMIT 50","createdAt":"2026-10-07T12:00:00Z","updatedAt":null}""";
    private const string Period = """{"from":"2026-10-07T00:00:00Z","to":"2026-10-08T00:00:00Z","granularity":"day","reads":0,"writes":0}""";
    private const string Series = """[{"start":"2026-10-07T00:00:00Z","reads":0,"writes":0}]""";
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
                Description = "Raw document key, supplied exactly once using UTF-8 query encoding. Decode once; slashes, dot segments and literal percent escapes remain distinct keys. No Unicode normalization. No other query parameters are accepted.",
                Schema = new OpenApiSchema { Type = JsonSchemaType.String, MinLength = 1, MaxLength = 256 },
                Example = JsonValue.Create("a/b"),
            });
        }

        if (operation.RequestBody?.Content is { } request)
        {
            foreach (var (mediaType, media) in request)
            {
                if (media is OpenApiMediaType body)
                {
                    body.Example = JsonNode.Parse(Request(id, mediaType));
                }
            }
        }

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
                if (id == "subscribeCollection" && status == "200")
                {
                    media.Example = JsonValue.Create($"event: shape\nid: h1-0000000000000000:1\ndata: {Response("getShape", status)}\n\n");
                }

                if (id == "getShape" && status == "409" && media.Example is JsonObject example)
                {
                    example["error"]!["handle"] = "h1-0000000000000000";
                }
            }
        }
    }

    private static string Request(string? id, string mediaType) => id switch
    {
        "createDatabase" => """{"name":"main"}""",
        "branchDatabase" => """{"name":"main_branch"}""",
        "updateDatabase" => """{"replicaRegions":[]}""",
        "restoreBackup" => """{"name":"restored"}""",
        "planPointInTime" => """{"pointInTime":"2026-10-07T12:00:13Z"}""",
        "setBackupPolicy" => """{"fullBackupCron":"0 2 * * *","retentionDays":7,"disabled":false}""",
        "createIndex" => """{"collection":"people","fields":["name"],"name":"people_name"}""",
        "setAutoIndexPolicy" => """{"enabled":false}""",
        "createCollection" => """{"name":"people"}""",
        "putDeclaredCollectionSchema" => DeclaredSchema,
        "declareCollectionSchema" => """{"fields":[{"name":"email","unique":true,"normalization":"case_fold"}],"indexes":[{"name":"tenant_handle","fields":["tenant","handle"],"unique":true}]}""",
        "createDocument" or "putDocument" => """{"name":"Ada"}""",
        "patchDocument" when mediaType == "application/vnd.terrabase.ops+json" => """{"ops":[{"op":"set","path":"/name","value":"Ada"}]}""",
        "patchDocument" => """{"name":"Ada"}""",
        "queryDocuments" => """{"filter":{"eq":["/name","Ada"]},"limit":50,"count":true}""",
        "explainSql" => """{"sql":"SELECT * FROM people WHERE email = 'person@example.com' LIMIT 10"}""",
        "runSql" or "checkSql" => """{"sql":"SELECT * FROM people LIMIT 50"}""",
        "createSavedQuery" or "updateSavedQuery" => """{"name":"People","sql":"SELECT * FROM people LIMIT 50"}""",
        "runTransaction" => """{"ops":[{"op":"create","collection":"people","document":{"name":"Ada"}}]}""",
        _ => "{}",
    };

    private static string Response(string? id, string status) => id switch
    {
        "healthz" => $$"""{"status":"ok","version":"{{ServiceVersion.Value}}","region":"gru-1"}""",
        "readyz" => $$"""{"status":"{{(status == "503" ? "unavailable" : "ready")}}","version":"{{ServiceVersion.Value}}","region":"gru-1"}""",
        "createDatabase" or "getDatabase" or "updateDatabase" => Database,
        "branchDatabase" => """{"id":"01ARZ3NDEKTSV4RRFFQ69G5FA6","projectId":"01ARZ3NDEKTSV4RRFFQ69G5FA2","name":"main_branch","branchedFrom":"01ARZ3NDEKTSV4RRFFQ69G5FA1","createdAt":"2026-10-07T12:00:00Z","region":"gru-1","homeRegion":"gru-1","replicaRegions":[]}""",
        "listDatabases" => Page(Database),
        "createBackup" => Backup,
        "listBackups" => Page(Backup),
        "restoreBackup" => """{"id":"01ARZ3NDEKTSV4RRFFQ69G5FA6","projectId":"01ARZ3NDEKTSV4RRFFQ69G5FA2","name":"restored","branchedFrom":"01ARZ3NDEKTSV4RRFFQ69G5FA1","createdAt":"2026-10-07T12:00:00Z","homeRegion":"gru-1","replicaRegions":[],"placementVersion":0}""",
        "getBackupPolicy" => Policy,
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
        "declareCollectionSchema" => """{"constraints":[{"name":"field_82244417f956ac7c","fields":["email"],"normalization":"case_fold"},{"name":"tenant_handle","fields":["tenant","handle"],"normalization":"none"}]}""",
        "getDatabaseUsage" => Usage.Replace("PERIOD", Period, StringComparison.Ordinal).Replace("SERIES", Series, StringComparison.Ordinal),
        "getProjectUsage" => "{\"projectId\":\"01ARZ3NDEKTSV4RRFFQ69G5FA2\",\"databases\":0,\"storageBytes\":0,\"logicalBytes\":0,\"documents\":0,\"reads\":0,\"writes\":0,\"period\":" + Period + ",\"series\":" + Series + ",\"items\":[]}",
        "createCollection" or "getCollection" or "dropCollection" => Collection,
        "getDeclaredCollectionSchema" or "putDeclaredCollectionSchema" => "{\"schema\":" + DeclaredSchema + ",\"dialect\":\"https://json-schema.org/draft/2020-12/schema\"}",
        "deleteDeclaredCollectionSchema" => """{"schema":null}""",
        "listCollections" => DataPage(Collection),
        "getDocument" => Document,
        "createDocument" or "putDocument" or "patchDocument" or "deleteDocument" => Write,
        "listDocuments" => DataPage(Document),
        "queryDocuments" => "{\"items\":[" + Document + "],\"next_cursor\":null,\"count\":{\"value\":\"1\",\"exact\":true}}",
        "runSql" => """{"matched":1,"truncated":false,"rows":[{"id":"01ARZ3NDEKTSV4RRFFQ69G5FA3","value":{"name":"Ada"}}]}""",
        "explainSql" => """{"ok":true,"error":null,"collection":"people","hasLimit":true,"plan":["SEARCH documents USING INDEX tb_index_people_email (collection=? AND <expr>=?)"],"indexes":["people_email"],"suggestions":[]}""",
        "checkSql" => """{"ok":true,"error":null,"collection":"people","hasLimit":true,"plan":[],"indexes":[],"suggestions":[]}""",
        "createSavedQuery" or "getSavedQuery" => Saved,
        "updateSavedQuery" => Saved.Replace("\"updatedAt\":null", "\"updatedAt\":\"2026-10-07T12:00:00Z\"", StringComparison.Ordinal),
        "listSavedQueries" => Page(Saved),
        "runTransaction" => """{"offset":"1","durability":"local_durable","results":[{"id":"01ARZ3NDEKTSV4RRFFQ69G5FA3","version":"opaque-version-from-server"}]}""",
        "getShape" => """{"shape_id":"00000000000000000000000000000001","handle":"h1-0000000000000000","offset":"1","next_offset":"1","up_to_date":true,"changes":[]}""",
        _ => "{}",
    };

    private static string Codes(string? id, string status) => status switch
    {
        "400" when id == "queryDocuments" => "invalid_request, invalid_query, invalid_cursor, sort_requires_index",
        "400" when id is "getDatabaseSchema" or "getCollectionSchema" or "runSql" or "checkSql" or "explainSql" or "getDatabaseUsage" or "getProjectUsage" =>
            "invalid_request, invalid_query",
        "400" when id == "putDeclaredCollectionSchema" => "invalid_request, invalid_schema",
        "400" when id is "createDocument" or "putDocument" => "invalid_request, schema_validation_failed",
        "400" when id == "patchDocument" => "invalid_request, invalid_patch, schema_validation_failed",
        "400" when id == "setAutoIndexPolicy" => "invalid_request, auto_index_policy_limit",
        "400" when id == "listCollections" => "invalid_request, invalid_cursor",
        "400" when id == "listDocuments" => "invalid_request, invalid_cursor, invalid_query",
        "400" when id == "runTransaction" => "invalid_request, invalid_patch, invalid_query, assert_requires_index, schema_validation_failed",
        "400" when id is "getShape" or "subscribeCollection" => "invalid_request, invalid_query",
        "400" => "invalid_request",
        "401" => "invalid_key",
        "403" when id is "createDatabase" or "branchDatabase" or "updateDatabase" or "deleteDatabase" or "restoreBackup" or "setBackupPolicy" or "setAutoIndexPolicy" or "createIndex" or "dropIndex" or "applyIndexRecommendation" or "createSavedQuery" or "updateSavedQuery" or "deleteSavedQuery" or "declareCollectionSchema" => "forbidden_scope, forbidden_role",
        "403" => "forbidden_scope",
        "404" when id is "createCollection" or "listCollections" => "unknown_database",
        "404" when id is "getDocument" or "patchDocument" or "deleteDocument" or "runTransaction" => "unknown_database, unknown_collection, not_found",
        "404" when id is "getDeclaredCollectionSchema" or "putDeclaredCollectionSchema" or "deleteDeclaredCollectionSchema" => "unknown_database, unknown_collection",
        "404" when id is "getCollection" or "dropCollection" or "getShape" or "subscribeCollection" or "listDocuments" or "createDocument" or "putDocument" or "queryDocuments" => "unknown_database, unknown_collection",
        "404" when id is "createDatabase" or "getDatabase" or "listDatabases" or "updateDatabase" or "deleteDatabase" or "branchDatabase" or "getDatabaseUsage" or "getProjectUsage" => "unknown_database, unknown_route",
        "404" => "unknown_database, unknown_route, not_found",
        "408" => "request_timeout",
        "409" when id is "getShape" or "subscribeCollection" => "must_refetch",
        "409" when id is "patchDocument" or "runTransaction" => "patch_conflict, unique_violation",
        "409" when id is "createDocument" or "putDocument" or "getDocument" => "unique_violation",
        "409" when id == "declareCollectionSchema" => "schema_requires_empty_collection",
        "409" when id == "deleteDatabase" => "database_replicated, wrong_region",
        "409" when id == "createIndex" => "index_exists",
        "409" when id is "createDatabase" or "branchDatabase" or "restoreBackup" or "createCollection" or "applyIndexRecommendation" => "name_taken",
        "409" => "conflict",
        "412" => "precondition_failed",
        "413" => "payload_too_large",
        "414" => "uri_too_long",
        "415" => "unsupported_media_type",
        "421" => "misdirected_request, wrong_region",
        "429" when id == "subscribeCollection" => "subscription_busy",
        "429" => "shape_busy",
        "503" when id is "dropCollection" or "runTransaction" => "capacity, storage_busy, unavailable",
        "503" when id == "planPointInTime" => "pitr_plan_limit, pitr_plan_timeout, pitr_plan_unavailable, storage_busy, catalog_primary_unavailable",
        "503" when id is "createDocument" or "putDocument" or "patchDocument" or "deleteDocument" => "capacity, storage_busy, unavailable",
        "503" when id is "queryDocuments" or "getShape" or "getDocument" or "listDocuments" or "createCollection" or "getCollection" or "listCollections" => "storage_busy, unavailable",
        "503" => "storage_busy, capacity, catalog_primary_unavailable, not_primary, unavailable",
        _ => "internal_error",
    };

    private static string Page(string item) => "{\"items\":[" + item + "],\"next\":null}";

    private static string DataPage(string item) => "{\"items\":[" + item + "],\"next_cursor\":null}";

    private static string Items(string item) => "{\"items\":[" + item + "]}";
}

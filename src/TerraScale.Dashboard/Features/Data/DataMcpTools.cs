using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using ModelContextProtocol.Server;
using TerraScale.Dashboard.Platform.Mcp;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.Dashboard.Features.Data;

[McpServerToolType]
public sealed class DataMcpTools(IHttpContextAccessor http, DataService data, SavedQueryService saved)
{
    [McpServerTool(Name = "list_collections", Title = "List collections", ReadOnly = true, Destructive = false, OpenWorld = false, Idempotent = true, UseStructuredContent = true), Description("List the collection names in a database.")]
    public async Task<McpCollectionList> Collections(string databaseId, string? organizationId = null, string? projectId = null) =>
        new((await data.CollectionsAsync(Scope(organizationId, projectId), databaseId, Cancellation)).Select(collection => collection.Name).ToArray());

    [McpServerTool(Name = "create_collection", Title = "Create collection", Destructive = false, OpenWorld = false, UseStructuredContent = true), Description("Create an empty collection in a database before writing documents to it. Names start with a lowercase letter and use up to 63 lowercase letters, digits, hyphens and underscores. Fails if the collection exists. Requires write access.")]
    public async Task<McpCollectionCreated> CreateCollection(string databaseId, string name, string? organizationId = null, string? projectId = null)
    {
        await data.CreateCollectionAsync(Scope(organizationId, projectId), databaseId, name, Cancellation);
        return new(name, "created");
    }

    [McpServerTool(Name = "list_documents", Title = "List documents", ReadOnly = true, Destructive = false, OpenWorld = false, Idempotent = true, UseStructuredContent = true), Description("List documents by ID using native cursor pagination: limit 1–100 (0 or omitted means 100), after is the opaque nextCursor from the previous response. Search filters each fetched page by case-insensitive ID/JSON substring; an empty page may still have hasMore=true. Continue until hasMore=false. Setting sort, descending=true or page!=1 selects browser paging: 25 rows per page, page 1–40, no limit/after. Browser search scans at most 1,000 documents; without search reads only through the requested page plus one row, up to 1,000. Browser results expose actual (possibly clamped) page, pageCount and total; totalIsLowerBound=true means the scan is incomplete and pageCount is also a lower bound. hasMore means later pages or unscanned documents remain, including beyond the browser cap; use cursor mode for complete traversal. Cursor mode has no total/page counts; browser mode has no nextCursor.")]
    public async Task<McpDocumentList> Documents(string databaseId, string collection, string search = "", int limit = 0, string after = "", string? organizationId = null, string? projectId = null,
        string sort = "", bool descending = false, int page = 1)
    {
        var context = Scope(organizationId, projectId);
        if (page is < 1 or > 40) throw new AccountAccessException("Document pages use 1–40.", 422);
        if (limit is < 0 or > 100) throw new AccountAccessException("Document limits use 1–100 rows, or 0 for the default of 100.", 422);
        if (sort.Length > 0 || descending || page != 1)
        {
            if (limit != 0 || after.Length > 0) throw new AccountAccessException("Browser paging (sort, descending or page) cannot be combined with limit or after. Use cursor paging for complete traversal.", 422);
            var browse = await data.BrowseAsync(context, databaseId, collection, search, sort, descending, page, Cancellation);
            return new(browse.Documents.Select(row => new McpDocument(row.Id, row.Document)).ToArray(),
                HasMore: browse.Page < browse.PageCount || browse.More, Page: browse.Page, PageCount: browse.PageCount, Total: browse.Matched, TotalIsLowerBound: browse.More);
        }
        var result = await data.ListPageAsync(context, databaseId, collection, search, limit, after, Cancellation);
        return new(result.Items.Select(row => new McpDocument(row.Id, row.Document)).ToArray(), result.NextCursor, !string.IsNullOrEmpty(result.NextCursor));
    }

    [McpServerTool(Name = "get_document", Title = "Get document", ReadOnly = true, Destructive = false, OpenWorld = false, Idempotent = true, UseStructuredContent = true), Description("Read one document by its ULID ID. Set includeVersion for a conditional-write version.")]
    public async Task<McpDocument> Document(string databaseId, string collection, string documentId = "", string? organizationId = null, string? projectId = null, string? id = null, bool includeVersion = false)
    {
        var row = await data.GetAsync(Scope(organizationId, projectId), databaseId, collection, McpScope.Alias(documentId, id, "documentId"), Cancellation);
        return new(row.Id, row.Document, includeVersion ? row.Version : null);
    }

    [McpServerTool(Name = "create_document", Title = "Create document", Destructive = false, OpenWorld = false, UseStructuredContent = true), Description("Create a document in a collection. Omit documentId to generate a ULID; a supplied ID must be a 26-character uppercase ULID. Fails if the ID already exists. Requires write access.")]
    public Task<McpDocumentWrite> CreateDocument(string databaseId, string collection, [Description("The document body as a JSON object. Required unless using the value alias.")] Dictionary<string, JsonElement>? document = null, string documentId = "", string? organizationId = null, string? projectId = null, JsonElement? value = null, string? id = null) =>
        WriteDocumentAsync(Scope(organizationId, projectId), databaseId, collection, "create", McpScope.Alias(documentId, id, "documentId", false), Body(document, value), "");

    [McpServerTool(Name = "replace_document", Title = "Replace document", Destructive = true, OpenWorld = false, Idempotent = true, UseStructuredContent = true), Description("Replace an existing document's whole body, addressed by its ULID ID. Requires write access. An omitted version uses the current version as a precondition.")]
    public Task<McpDocumentWrite> ReplaceDocument(string databaseId, string collection, string documentId = "", [Description("The new document body as a JSON object. Required unless using the value alias.")] Dictionary<string, JsonElement>? document = null, string? version = null, string? organizationId = null, string? projectId = null, string? id = null, JsonElement? value = null) =>
        WriteDocumentAsync(Scope(organizationId, projectId), databaseId, collection, "replace", McpScope.Alias(documentId, id, "documentId"), Body(document, value), version);

    [McpServerTool(Name = "patch_document", Title = "Patch document", Destructive = false, OpenWorld = false, UseStructuredContent = true), Description("Merge-patch an existing document (RFC 7396), addressed by its ULID ID. Requires write access. An omitted version uses the current version as a precondition.")]
    public Task<McpDocumentWrite> PatchDocument(string databaseId, string collection, string documentId = "", [Description("RFC 7396 JSON merge patch: null removes fields and nested objects merge. Required unless using the value alias.")] Dictionary<string, JsonElement>? patch = null, string? version = null, string? organizationId = null, string? projectId = null, string? id = null, JsonElement? value = null) =>
        WriteDocumentAsync(Scope(organizationId, projectId), databaseId, collection, "patch", McpScope.Alias(documentId, id, "documentId"), Body(patch, value), version);

    [McpServerTool(Name = "delete_document", Title = "Delete document", Destructive = true, OpenWorld = false, UseStructuredContent = true), Description("Delete a document by its ULID ID. Requires write access. An omitted version uses the current version as a precondition.")]
    public Task<McpDocumentWrite> DeleteDocument(string databaseId, string collection, string documentId = "", string? version = null, string? organizationId = null, string? projectId = null, string? id = null) =>
        WriteDocumentAsync(Scope(organizationId, projectId), databaseId, collection, "delete", McpScope.Alias(documentId, id, "documentId"), default, version);

    [McpServerTool(Name = "get_document_history", Title = "Get document history", ReadOnly = true, Destructive = false, OpenWorld = false, Idempotent = true, UseStructuredContent = true), Description("List a document's recorded versions (documentId is a ULID), oldest first, including deletions. Requires a producer history contract.")]
    public Task<McpDocumentHistory> History(string databaseId, string collection, string documentId, string? organizationId = null, string? projectId = null)
    {
        ValidateCollection(Scope(organizationId, projectId), databaseId, collection);
        DataService.ValidDocumentId(documentId);
        throw new AccountAccessException("The data plane does not expose document history.", 503);
    }

    [McpServerTool(Name = "get_collection_schema", Title = "Get collection schema", ReadOnly = true, Destructive = false, OpenWorld = false, Idempotent = true, UseStructuredContent = true), Description("Read a collection's schema.")]
    public async Task<McpCollectionSchema> Schema(string databaseId, string collection, string? organizationId = null, string? projectId = null) =>
        new(collection, await data.SchemaAsync(Scope(organizationId, projectId), databaseId, collection, Cancellation));

    [McpServerTool(Name = "set_collection_schema", Title = "Set collection schema", Destructive = false, OpenWorld = false, Idempotent = true, UseStructuredContent = true), Description("Apply a collection schema. Requires an owner or admin role and a producer schema-update contract.")]
    public Task<McpSchemaApplied> SetSchema(string databaseId, string collection, Dictionary<string, JsonElement> schema, string? organizationId = null, string? projectId = null)
    {
        var context = Scope(organizationId, projectId);
        ValidateCollection(context, databaseId, collection);
        McpScope.DemandManagement(context);
        if (schema is null) throw new AccountAccessException("Schema must be a JSON object.", 422);
        throw new AccountAccessException("The data plane does not expose editable collection schemas.", 503);
    }

    [McpServerTool(Name = "execute_sql", ReadOnly = true, Destructive = false, OpenWorld = false, Idempotent = true, UseStructuredContent = true), Description("Execute one bounded read-only SQL statement with the same checks as the dashboard editor.")]
    public Task<DatabaseSqlResult> Sql(string databaseId, string sql, string? organizationId = null, string? projectId = null) => data.SqlAsync(Scope(organizationId, projectId), databaseId, sql, false, Cancellation);

    [McpServerTool(Name = "check_sql", Title = "Check SQL", ReadOnly = true, Destructive = false, OpenWorld = false, Idempotent = true, UseStructuredContent = true), Description("Check SQL input limits without running it: blank input, size, statement boundaries, parameters and transaction commands. Does not validate SQL syntax, tables or permissions.")]
    public SqlAnalysis CheckSql(string databaseId, string sql, string? organizationId = null, string? projectId = null)
    {
        DatabaseService.Database(Scope(organizationId, projectId), databaseId);
        return SqlValidation.Analyze(sql);
    }

    [McpServerTool(Name = "run_sql", Title = "Run SQL", ReadOnly = true, Destructive = false, OpenWorld = false, Idempotent = true, UseStructuredContent = true), Description("Run one read-only SQL query against a database.")]
    public async Task<McpSqlResponse> RunSql(string databaseId, string sql, string? organizationId = null, string? projectId = null)
    {
        var result = await data.SqlAsync(Scope(organizationId, projectId), databaseId, sql, false, Cancellation);
        return new(new(result.Columns, result.Rows));
    }

    [McpServerTool(Name = "list_indexes", Title = "List indexes", ReadOnly = true, Destructive = false, OpenWorld = false, Idempotent = true, UseStructuredContent = true), Description("List a collection's indexed fields and their build state.")]
    public async Task<McpIndexResponse> Indexes(string databaseId, string collection, string? organizationId = null, string? projectId = null)
    {
        collection = DataService.ValidCollection(collection);
        var indexes = await data.IndexesAsync(Scope(organizationId, projectId), databaseId, Cancellation);
        return new(collection, indexes.Where(index => index.Collection == collection).Select(index => new McpIndex(string.Join(", ", index.Fields), index.State)).ToArray());
    }

    [McpServerTool(Name = "create_index", Title = "Create index", Destructive = false, OpenWorld = false, Idempotent = true, UseStructuredContent = true), Description("Index one field of a collection. Requires an owner or admin role.")]
    public async Task<McpIndexCreated> CreateIndex(string databaseId, string collection, string field, string? organizationId = null, string? projectId = null)
    {
        collection = DataService.ValidCollection(collection);
        await data.CreateIndexFieldAsync(Scope(organizationId, projectId), databaseId, collection, field, Cancellation);
        return new(collection, field, "created");
    }

    [McpServerTool(Name = "list_index_recommendations", Title = "List index recommendations", ReadOnly = true, Destructive = false, OpenWorld = false, Idempotent = true, UseStructuredContent = true), Description("List suggested indexes for a database. Requires a producer recommendation contract.")]
    public Task<McpRecommendations> Recommendations(string databaseId, string collection = "", string? organizationId = null, string? projectId = null)
    {
        var context = Scope(organizationId, projectId);
        DatabaseService.Database(context, databaseId);
        if (collection.Length > 0) DataService.ValidCollection(collection);
        throw new AccountAccessException("The data plane does not expose index recommendations.", 503);
    }

    [McpServerTool(Name = "apply_index_recommendation", Title = "Apply index recommendation", Destructive = false, OpenWorld = false, Idempotent = true, UseStructuredContent = true), Description("Build the indexes an index recommendation suggests. Requires an owner or admin role and a producer recommendation contract.")]
    public Task<McpRecommendationApplied> ApplyRecommendation(string recommendationId, string databaseId = "", string collection = "", string? organizationId = null, string? projectId = null)
    {
        var context = Scope(organizationId, projectId);
        McpScope.DemandManagement(context);
        if (databaseId.Length > 0) DatabaseService.Database(context, databaseId);
        if (collection.Length > 0) DataService.ValidCollection(collection);
        if (recommendationId.Length is 0 or > 128) throw new AccountAccessException("Choose a valid recommendation ID.", 422);
        throw new AccountAccessException("The data plane does not expose index recommendation application.", 503);
    }

    [McpServerTool(Name = "get_index_migration", Title = "Get index migration", ReadOnly = true, Destructive = false, OpenWorld = false, Idempotent = true, UseStructuredContent = true), Description("Show index backfill progress for a collection. Requires a producer migration contract.")]
    public Task<McpIndexMigration> Migration(string databaseId, string collection, string? organizationId = null, string? projectId = null)
    {
        ValidateCollection(Scope(organizationId, projectId), databaseId, collection);
        throw new AccountAccessException("The data plane does not expose index migration progress.", 503);
    }

    [McpServerTool(Name = "list_saved_queries", Title = "List saved queries", ReadOnly = true, Destructive = false, OpenWorld = false, Idempotent = true, UseStructuredContent = true), Description("List the SQL queries saved in a project.")]
    public async Task<McpSavedQueries> SavedQueries(string? organizationId = null, string? projectId = null) =>
        new((await saved.ListAsync(Scope(organizationId, projectId), Cancellation)).Select(SavedQueryOutput).ToArray());

    [McpServerTool(Name = "save_query", Title = "Save query", Destructive = false, OpenWorld = false, UseStructuredContent = true), Description("Save a read-only SQL query for everyone in the project. Requires write access.")]
    public async Task<McpSavedQuery> SaveQuery(string name, string sql, string? organizationId = null, string? projectId = null) =>
        SavedQueryOutput(await saved.SaveAsync(Scope(organizationId, projectId), "", name, sql, Cancellation));

    [McpServerTool(Name = "delete_saved_query", Title = "Delete saved query", Destructive = true, OpenWorld = false, UseStructuredContent = true), Description("Delete a saved query from the project. Requires write access.")]
    public async Task<McpDeleted> DeleteSavedQuery(string queryId, string? organizationId = null, string? projectId = null)
    {
        await saved.DeleteAsync(Scope(organizationId, projectId), "", queryId, Cancellation);
        return new(true);
    }

    private static McpSavedQuery SavedQueryOutput(SavedQuery query) => new(query.Id, query.Name, query.Sql, query.CreatedAt);

    private async Task<McpDocumentWrite> WriteDocumentAsync(AccountContext context, string databaseId, string collection, string operation, string id, JsonElement body, string? version)
    {
        if (!context.CanWrite) throw new AccountAccessException("Write access is required.");
        if (operation != "create" && version is null)
        {
            var current = await data.GetAsync(context, databaseId, collection, id, Cancellation);
            if (current.State != "value") throw new IOException("The document has no unambiguous current version.");
            version = current.Version;
        }
        if (operation != "create" && string.IsNullOrWhiteSpace(version)) throw new AccountAccessException("A document version is required.", 422);
        try
        {
            var result = await data.WriteAsync(context, databaseId, collection, operation, id, operation == "delete" ? "" : body.GetRawText(), version ?? "", Cancellation);
            return new(result.Id, operation);
        }
        catch (DocumentWriteOutcomeUnknownException error)
        {
            return new(error.DocumentId, operation) { Outcome = "unknown", OperationId = error.OperationId, Message = error.Message };
        }
    }

    private static JsonElement Body(Dictionary<string, JsonElement>? document, JsonElement? value)
    {
        var primary = document is null ? default : JsonSerializer.SerializeToElement(document);
        var alias = value ?? default;
        if (primary.ValueKind != JsonValueKind.Undefined && alias.ValueKind != JsonValueKind.Undefined && primary.GetRawText() != alias.GetRawText())
            throw new AccountAccessException("Conflicting document bodies were provided.", 422);
        var body = primary.ValueKind == JsonValueKind.Undefined ? alias : primary;
        if (body.ValueKind != JsonValueKind.Object) throw new AccountAccessException("Document must be a JSON object.", 422);
        return body;
    }

    private static void ValidateCollection(AccountContext context, string databaseId, string collection)
    { DatabaseService.Database(context, databaseId); DataService.ValidCollection(collection); }

    private AccountContext Scope(string? organizationId, string? projectId) => DashboardMcp.Scope(http.HttpContext, organizationId, projectId);
    private CancellationToken Cancellation => http.HttpContext?.RequestAborted ?? default;
}

public sealed record McpCollectionList(IReadOnlyList<string> Collections);
public sealed record McpCollectionCreated(string Collection, string Status);
public sealed record McpDocument(string Id, JsonElement Value, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Version = null);
public sealed record McpDocumentList(IReadOnlyList<McpDocument> Documents, string? NextCursor = null, bool HasMore = false,
    int? Page = null, int? PageCount = null, int? Total = null, bool? TotalIsLowerBound = null);
public sealed record McpDocumentWrite(string Id, string Operation)
{
    public string Outcome { get; init; } = "acknowledged";
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? OperationId { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Message { get; init; }
}
public sealed record McpHistoryVersion(string Revision, bool Deleted, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] JsonElement? Value);
public sealed record McpDocumentHistory(IReadOnlyList<McpHistoryVersion> Versions);
public sealed record McpCollectionSchema(string Collection, TerraBaseCollectionSchema Schema);
public sealed record McpSchemaApplied(string Collection, string Status);
public sealed record McpSqlRows(IReadOnlyList<DatabaseSqlColumn> Columns, IReadOnlyList<IReadOnlyList<JsonElement>> Rows);
public sealed record McpSqlResponse(McpSqlRows Result);
public sealed record McpIndex(string Field, string State);
public sealed record McpIndexResponse(string Collection, IReadOnlyList<McpIndex> Indexes);
public sealed record McpIndexCreated(string Collection, string Field, string Status);
public sealed record McpRecommendation(string Id, string Collection, IReadOnlyList<string> Fields, string Reason, string State);
public sealed record McpRecommendations(IReadOnlyList<McpRecommendation> Recommendations);
public sealed record McpRecommendationApplied(string RecommendationId, string DatabaseId, string Collection, string Status);
public sealed record McpMigration(string State, int Completed, int Total, int Percent, string Watermark);
public sealed record McpIndexMigration([property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] McpMigration? Migration);
public sealed record McpSavedQuery(string Id, string Name, string Sql, DateTimeOffset CreatedAt);
public sealed record McpSavedQueries(IReadOnlyList<McpSavedQuery> Queries);

using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using ModelContextProtocol.Server;
using TerraScale.Dashboard.Platform.Mcp;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.Dashboard.Features.Data;

[McpServerToolType]
public sealed class DataMcpTools(IHttpContextAccessor http, DataService data, SavedQueryService saved, AccountService accounts)
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

    [McpServerTool(Name = "list_documents", Title = "List documents", ReadOnly = true, Destructive = false, OpenWorld = false, Idempotent = true, UseStructuredContent = true), Description("List documents using opaque after/nextCursor pagination; keep database, collection, search, sort and descending unchanged while continuing. Native ID order defaults to 100 rows; sort or descending selects query order, default 25. limit 1–100 overrides either default; 0 retains the default. Search filters case-insensitive ID/JSON substrings. Query search examines at most 100 documents in four reads per response, so empty pages may have hasMore=true. Continue until hasMore=false; nextCursor is explicitly null at the end and limit reports the effective size. page is only a positive query-page label and requires after beyond page 1; native mode uses after without page. No total or pageCount is calculated.")]
    public async Task<McpDocumentList> Documents(string databaseId, string collection, string search = "", int limit = 0, string after = "", string? organizationId = null, string? projectId = null,
        string sort = "", bool descending = false,
        [Description("Query-order page label, used only with sort or descending; pages beyond 1 also need after. Native ID order ignores labels and continues with after alone.")] int page = 1)
    {
        var context = Scope(organizationId, projectId);
        if (page is < 1 or >= int.MaxValue) throw new AccountAccessException("Document page labels must be positive and below Int32.MaxValue.", 422);
        if (limit is < 0 or > 100) throw new AccountAccessException("Document limits use 1–100 rows, or 0 for the default.", 422);
        if (after.Length > 0 && (string.IsNullOrWhiteSpace(after) || after.Any(char.IsControl))) throw new AccountAccessException("Enter a valid document continuation cursor.", 422);
        if (sort.Length > 0 || descending)
        {
            var size = limit == 0 ? 25 : limit;
            var browse = await data.BrowseAsync(context, databaseId, collection, search, sort, descending, page, Cancellation, after, size);
            return new(browse.Documents.Select(row => new McpDocument(row.Id, row.Document)).ToArray(),
                browse.NextCursor.Length == 0 ? null : browse.NextCursor, browse.More, Page: browse.Page, Limit: size);
        }
        if (page != 1) throw new AccountAccessException("page applies only with sort or descending. In native ID order, omit page and continue with after set to the previous nextCursor.", 422);
        var result = await data.ListPageAsync(context, databaseId, collection, search, limit, after, Cancellation);
        return new(result.Items.Select(row => new McpDocument(row.Id, row.Document)).ToArray(), result.NextCursor, !string.IsNullOrEmpty(result.NextCursor), Limit: limit == 0 ? 100 : limit);
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

    [McpServerTool(Name = "patch_document", Title = "Patch document", Destructive = true, OpenWorld = false, UseStructuredContent = true), Description("Merge-patch an existing document (RFC 7396), addressed by its ULID ID. Null values delete fields. Requires write access. An omitted version uses the current version as a precondition.")]
    public Task<McpDocumentWrite> PatchDocument(string databaseId, string collection, string documentId = "", [Description("RFC 7396 JSON merge patch: null removes fields and nested objects merge. Required unless using the value alias.")] Dictionary<string, JsonElement>? patch = null, string? version = null, string? organizationId = null, string? projectId = null, string? id = null, JsonElement? value = null) =>
        WriteDocumentAsync(Scope(organizationId, projectId), databaseId, collection, "patch", McpScope.Alias(documentId, id, "documentId"), Body(patch, value), version);

    [McpServerTool(Name = "delete_document", Title = "Delete document", Destructive = true, OpenWorld = false, UseStructuredContent = true), Description("Delete a document by its ULID ID. Requires write access. An omitted version uses the current version as a precondition.")]
    public Task<McpDocumentWrite> DeleteDocument(string databaseId, string collection, string documentId = "", string? version = null, string? organizationId = null, string? projectId = null, string? id = null) =>
        WriteDocumentAsync(Scope(organizationId, projectId), databaseId, collection, "delete", McpScope.Alias(documentId, id, "documentId"), default, version);

    [McpServerTool(Name = "get_collection_schema", Title = "Get collection schema", ReadOnly = true, Destructive = false, OpenWorld = false, Idempotent = true, UseStructuredContent = true), Description("Read inferred sampled fields, separate from declared JSON Schema validation. Use get_collection_validation for declared validation.")]
    public async Task<McpCollectionSchema> Schema(string databaseId, string collection, string? organizationId = null, string? projectId = null) =>
        new(collection, await data.SchemaAsync(Scope(organizationId, projectId), databaseId, collection, Cancellation));

    [McpServerTool(Name = "execute_sql", ReadOnly = true, Destructive = false, OpenWorld = false, Idempotent = true, UseStructuredContent = true), Description("Execute one read-only SQL statement against relational tables, which are separate from document collections. SQL is limited to 16 KiB; bound parameters are unsupported.")]
    public Task<DatabaseSqlResult> Sql(string databaseId, string sql, string? organizationId = null, string? projectId = null) => data.SqlAsync(Scope(organizationId, projectId), databaseId, sql, false, Cancellation);

    [McpServerTool(Name = "check_sql", Title = "Check SQL", ReadOnly = true, Destructive = false, OpenWorld = false, Idempotent = true, UseStructuredContent = true), Description("Check SQL input limits without running it: blank input, size, statement boundaries, parameters and transaction commands. Does not validate SQL syntax, tables or permissions.")]
    public SqlAnalysis CheckSql(string databaseId, string sql, string? organizationId = null, string? projectId = null)
    {
        DatabaseService.Database(Scope(organizationId, projectId), databaseId);
        return SqlValidation.Analyze(sql);
    }

    [McpServerTool(Name = "run_sql", Title = "Run SQL", ReadOnly = true, Destructive = false, OpenWorld = false, Idempotent = true, UseStructuredContent = true), Description("Run one read-only SQL query against relational tables, which are separate from document collections. SQL is limited to 16 KiB; bound parameters are unsupported.")]
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

    [McpServerTool(Name = "list_saved_queries", Title = "List saved queries", ReadOnly = true, Destructive = false, OpenWorld = false, Idempotent = true, UseStructuredContent = true), Description("List the SQL queries saved in a project.")]
    public async Task<McpSavedQueries> SavedQueries(string? organizationId = null, string? projectId = null,
        [Description(CursorPaging.Description)] int limit = 100, string after = "")
    {
        var context = Scope(organizationId, projectId);
        var page = CursorPaging.Page((await saved.ListAsync(context, Cancellation)).Select(SavedQueryOutput), query => query.Id,
            CursorPaging.Scope(context, "saved-queries"), limit, after);
        return new(page.Items, page.NextCursor, page.HasMore, page.Limit);
    }

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
        context = await AccountScope.RefreshAsync(accounts, context, Cancellation, requireProject: true);
        if (!context.CanWriteProjectData) throw new AccountAccessException("Write access is required.");
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

    private AccountContext Scope(string? organizationId, string? projectId) => DashboardMcp.Scope(http.HttpContext, organizationId, projectId);
    private CancellationToken Cancellation => http.HttpContext?.RequestAborted ?? default;
}

public sealed record McpCollectionList(IReadOnlyList<string> Collections);
public sealed record McpCollectionCreated(string Collection, string Status);
public sealed record McpDocument(string Id, JsonElement Value, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Version = null);
public sealed record McpDocumentList(IReadOnlyList<McpDocument> Documents,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? NextCursor = null, bool HasMore = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] int? Page = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] int? PageCount = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] int? Total = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] bool? TotalIsLowerBound = null, int Limit = 100);
public sealed record McpDocumentWrite(string Id, string Operation)
{
    public string Outcome { get; init; } = "acknowledged";
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? OperationId { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Message { get; init; }
}
public sealed record McpCollectionSchema(string Collection, TerraBaseCollectionSchema Schema);
public sealed record McpSchemaApplied(string Collection, string Status);
public sealed record McpSqlRows(IReadOnlyList<DatabaseSqlColumn> Columns, IReadOnlyList<IReadOnlyList<JsonElement>> Rows);
public sealed record McpSqlResponse(McpSqlRows Result);
public sealed record McpIndex(string Field, string State);
public sealed record McpIndexResponse(string Collection, IReadOnlyList<McpIndex> Indexes);
public sealed record McpIndexCreated(string Collection, string Field, string Status);
public sealed record McpSavedQuery(string Id, string Name, string Sql, DateTimeOffset CreatedAt);
public sealed record McpSavedQueries(IReadOnlyList<McpSavedQuery> Queries, [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? NextCursor, bool HasMore, int Limit);

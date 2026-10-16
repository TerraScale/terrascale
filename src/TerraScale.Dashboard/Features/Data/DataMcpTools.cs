using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using ModelContextProtocol.Server;
using TerraScale.Dashboard.Features.Accounts;
using TerraScale.Dashboard.Features.Databases;
using TerraScale.Dashboard.Platform.Mcp;
using TerraScale.DotNet.TerraBase;
using static TerraScale.Dashboard.Platform.Mcp.McpScope;

namespace TerraScale.Dashboard.Features.Data;

public record McpDatabase(string Id, string Name, string Status, string Region, IReadOnlyList<string> Placement, DateTimeOffset CreatedAt);
public sealed record McpDatabaseList(IReadOnlyList<McpDatabase> Databases);
public sealed record McpDatabaseDetail(string Id, string Name, string Status, string Region, IReadOnlyList<string> Placement, DateTimeOffset CreatedAt,
    string Endpoint, IReadOnlyList<string> Regions,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Collections,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? Documents,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? Bytes,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Unavailable, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? BackupsEnabled, bool CanManage, bool CanBranch);
public sealed record McpDeleted(bool Deleted);
public sealed record McpCollectionList(IReadOnlyList<string> Collections);
public sealed record McpDocument(string Id, JsonElement Value, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Version = null);
public sealed record McpDocumentList(IReadOnlyList<McpDocument> Documents);
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
public sealed record McpSqlRows(IReadOnlyList<DatabaseSqlRow> Rows);
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
public sealed record McpCredentialList(IReadOnlyList<ProjectCredentialMetadata> Credentials);
public sealed record McpCredentialRevoked(string KeyId, bool Revoked);

public sealed partial class DashboardMcpTools
{
    [McpServerTool(Name = "list_databases", Title = "List databases", ReadOnly = true, Destructive = false, OpenWorld = false, Idempotent = true, UseStructuredContent = true), Description("List the databases in a project with their status and region.")]
    public Task<McpDatabaseList> ListDatabases(string? organizationId = null, string? projectId = null)
    {
        var context = Scope(organizationId, projectId);
        DatabaseService.Scope(context);
        return Task.FromResult(new McpDatabaseList(context.Project!.Databases.Values.OrderBy(db => db.Name, StringComparer.Ordinal).Select(DatabaseOutput).ToArray()));
    }

    [McpServerTool(Name = "get_database", Title = "Get database", ReadOnly = true, Destructive = false, OpenWorld = false, Idempotent = true, UseStructuredContent = true), Description("Show one database's status, regions, storage totals, and backup state.")]
    public async Task<McpDatabaseDetail> GetDatabase(string databaseId, string? organizationId = null, string? projectId = null)
    {
        var context = Scope(organizationId, projectId);
        var db = DatabaseService.Database(context, databaseId);
        var placement = await databases.PlacementAsync(DatabaseService.Regions(db), Cancellation, DatabaseService.Regions(db));
        IReadOnlyList<CollectionStats>? contents = null;
        string? unavailable = null;
        try
        {
            var reported = await backend.CollectionsAsync(DatabaseService.Scope(context), await databases.HomeAsync(context, db, Cancellation), db.Id, Cancellation);
            DatabaseService.ValidateContents(reported);
            contents = reported;
        }
        catch (Exception error) when ((DatabaseEndpoints.KnownFailure(error) || error is HttpRequestException or JsonException or OverflowException or OperationCanceledException) &&
            (error is not OperationCanceledException || !Cancellation.IsCancellationRequested))
        { unavailable = "Database storage measurements are unavailable."; }
        bool? backupsEnabled = null;
        try
        {
            var listing = await backups.ListAsync(context, db.Id, Cancellation);
            if (listing.Policy is { } policy) backupsEnabled = !policy.Disabled && policy.FullBackupCron.Length > 0;
            else unavailable = unavailable is null ? "Backup scheduling measurements are unavailable." : unavailable + " Backup scheduling measurements are unavailable.";
        }
        catch (Exception error) when (BackupEndpoints.KnownFailure(error) &&
            (error is not OperationCanceledException || !Cancellation.IsCancellationRequested))
        { unavailable = unavailable is null ? "Backup measurements are unavailable." : unavailable + " Backup measurements are unavailable."; }
        return new(db.Id, db.Name, db.Status, db.Region, DatabaseService.Regions(db), db.CreatedAt, db.Endpoint,
            // MCP labels are an existing response contract, independent of page display casing.
            placement.Select(region => region.Code.ToUpperInvariant() switch { "SFO" => "SFO · San Francisco", "GRU" => "GRU · São Paulo", "" => "Not configured", var code => code }).ToArray(), contents?.Count, contents?.Sum(item => item.Documents), contents?.Sum(item => item.Bytes),
            unavailable, backupsEnabled, context.CanManage, db.Status == "ready" && context.CanManage && context.Organization!.CanProvision(context.UserId));
    }

    [McpServerTool(Name = "create_database", Title = "Create database", Destructive = false, OpenWorld = false, UseStructuredContent = true), Description("Provision a new database in a project. Requires an owner or admin role and a plan with database capacity.")]
    public async Task<McpDatabase> CreateDatabase(string name, string? region = null, string[]? regions = null, string? organizationId = null, string? projectId = null)
    {
        var context = Scope(organizationId, projectId);
        var placement = regions is { Length: > 0 } ? regions : [string.IsNullOrEmpty(region) ? context.Project!.Region : region];
        return DatabaseOutput(await databases.CreateAsync(context, name, placement, Cancellation));
    }

    [McpServerTool(Name = "branch_database", Title = "Branch database", Destructive = false, OpenWorld = false, UseStructuredContent = true), Description("Create a new database that copies a ready database's documents, schemas, and indexes.")]
    public async Task<McpDatabase> BranchDatabase(string databaseId, string name = "", string? organizationId = null, string? projectId = null) =>
        DatabaseOutput(await databases.BranchAsync(Scope(organizationId, projectId), databaseId, name, Cancellation));

    [McpServerTool(Name = "set_database_regions", Title = "Set database regions", Destructive = false, OpenWorld = false, UseStructuredContent = true), Description("Choose where a database lives: its home region alone, or with replica regions that replicate it. The home region never changes. Requires an owner or admin role.")]
    public async Task<McpDatabase> SetRegions(string databaseId, string[] regions, string? organizationId = null, string? projectId = null)
    {
        var context = Scope(organizationId, projectId);
        await databases.SetRegionsAsync(context, databaseId, regions, Cancellation);
        var fresh = await accounts.GetContextAsync(context.Session, Cancellation);
        var db = fresh.Organizations.First(org => org.Id == context.Organization!.Id).Projects[context.Project!.Id].Databases[databaseId];
        return DatabaseOutput(db);
    }

    [McpServerTool(Name = "delete_database", Title = "Delete database", Destructive = true, OpenWorld = false, UseStructuredContent = true), Description("Permanently delete a database and all of its data. Requires confirmName to equal the database name.")]
    public async Task<McpDeleted> DeleteDatabase(string databaseId, string confirmName = "", string? organizationId = null, string? projectId = null, string? confirmation = null)
    {
        await databases.DeleteAsync(Scope(organizationId, projectId), databaseId, Alias(confirmName, confirmation, "confirmName"), Cancellation);
        return new(true);
    }

    [McpServerTool(Name = "list_backups", Title = "List backups", ReadOnly = true, Destructive = false, OpenWorld = false, Idempotent = true, UseStructuredContent = true), Description("List a database's backups and its backup policy.")]
    public Task<BackupListing> ListBackups(string databaseId, string? organizationId = null, string? projectId = null) =>
        backups.ListAsync(Scope(organizationId, projectId), databaseId, Cancellation);

    [McpServerTool(Name = "create_backup", Title = "Create backup", Destructive = false, OpenWorld = false, UseStructuredContent = true), Description("Take an on-demand backup of a database's documents, schemas, and indexes. Requires an owner or admin role.")]
    public Task<BackupView> CreateBackup(string databaseId, string? organizationId = null, string? projectId = null) =>
        backups.CreateAsync(Scope(organizationId, projectId), databaseId, Cancellation);

    [McpServerTool(Name = "set_backup_policy", Title = "Set backup policy", Destructive = false, OpenWorld = false, Idempotent = true, UseStructuredContent = true), Description("Save a database's full-backup schedule and retention. Incremental schedules are unavailable. Requires an owner or admin role.")]
    public Task<BackupPolicyView> SetBackupPolicy(string databaseId, string fullBackupCron, string incrementalBackupCron, int retentionDays, bool disabled = false,
        string? organizationId = null, string? projectId = null) =>
        backups.SetPolicyAsync(Scope(organizationId, projectId), databaseId, fullBackupCron, incrementalBackupCron, retentionDays, disabled, Cancellation);

    [McpServerTool(Name = "restore_backup", Title = "Restore backup", Destructive = false, OpenWorld = false, UseStructuredContent = true), Description("Restore a database backup into a new database; the source database is unchanged. Requires an owner or admin role and provisioning rights.")]
    public Task<BackupRestoreResult> RestoreBackup(string databaseId, string name = "", string backupId = "", string? organizationId = null, string? projectId = null) =>
        backups.RestoreAsync(Scope(organizationId, projectId), databaseId, backupId, name, Cancellation);

    [McpServerTool(Name = "list_collections", Title = "List collections", ReadOnly = true, Destructive = false, OpenWorld = false, Idempotent = true, UseStructuredContent = true), Description("List the collection names in a database.")]
    public async Task<McpCollectionList> Collections(string databaseId, string? organizationId = null, string? projectId = null) =>
        new((await data.CollectionsAsync(Scope(organizationId, projectId), databaseId, Cancellation)).Select(collection => collection.Name).ToArray());

    [McpServerTool(Name = "list_documents", Title = "List documents", ReadOnly = true, Destructive = false, OpenWorld = false, Idempotent = true, UseStructuredContent = true), Description("List a collection's documents ordered by ID, with optional search and paging.")]
    public async Task<McpDocumentList> Documents(string databaseId, string collection, string search = "", int limit = 0, string after = "", string? organizationId = null, string? projectId = null,
        string sort = "", bool descending = false, int page = 1)
    {
        var context = Scope(organizationId, projectId);
        var rows = sort.Length > 0 || descending || page != 1
            ? (await data.BrowseAsync(context, databaseId, collection, search, sort, descending, page, Cancellation)).Documents
            : await data.ListAsync(context, databaseId, collection, search, limit, after, Cancellation);
        return new(rows.Select(row => new McpDocument(row.Id, row.Document)).ToArray());
    }

    [McpServerTool(Name = "get_document", Title = "Get document", ReadOnly = true, Destructive = false, OpenWorld = false, Idempotent = true, UseStructuredContent = true), Description("Read one document by ID. Set includeVersion for a conditional-write version.")]
    public async Task<McpDocument> Document(string databaseId, string collection, string documentId = "", string? organizationId = null, string? projectId = null, string? id = null, bool includeVersion = false)
    {
        var row = await data.GetAsync(Scope(organizationId, projectId), databaseId, collection, Alias(documentId, id, "documentId"), Cancellation);
        return new(row.Id, row.Document, includeVersion ? row.Version : null);
    }

    [McpServerTool(Name = "create_document", Title = "Create document", Destructive = false, OpenWorld = false, UseStructuredContent = true), Description("Create a document in a collection. Fails if the ID already exists. Requires write access.")]
    public Task<McpDocumentWrite> CreateDocument(string databaseId, string collection, [Description("The document body as a JSON object. Required unless using the value alias.")] Dictionary<string, JsonElement>? document = null, string documentId = "", string? organizationId = null, string? projectId = null, JsonElement? value = null, string? id = null) =>
        WriteDocumentAsync(Scope(organizationId, projectId), databaseId, collection, "create", Alias(documentId, id, "documentId", false), Body(document, value), "");

    [McpServerTool(Name = "replace_document", Title = "Replace document", Destructive = true, OpenWorld = false, Idempotent = true, UseStructuredContent = true), Description("Replace an existing document's whole body. Requires write access. An omitted version uses the current version as a precondition.")]
    public Task<McpDocumentWrite> ReplaceDocument(string databaseId, string collection, string documentId = "", [Description("The new document body as a JSON object. Required unless using the value alias.")] Dictionary<string, JsonElement>? document = null, string? version = null, string? organizationId = null, string? projectId = null, string? id = null, JsonElement? value = null) =>
        WriteDocumentAsync(Scope(organizationId, projectId), databaseId, collection, "replace", Alias(documentId, id, "documentId"), Body(document, value), version);

    [McpServerTool(Name = "patch_document", Title = "Patch document", Destructive = false, OpenWorld = false, UseStructuredContent = true), Description("Merge-patch an existing document (RFC 7396). Requires write access. An omitted version uses the current version as a precondition.")]
    public Task<McpDocumentWrite> PatchDocument(string databaseId, string collection, string documentId = "", [Description("RFC 7396 JSON merge patch: null removes fields and nested objects merge. Required unless using the value alias.")] Dictionary<string, JsonElement>? patch = null, string? version = null, string? organizationId = null, string? projectId = null, string? id = null, JsonElement? value = null) =>
        WriteDocumentAsync(Scope(organizationId, projectId), databaseId, collection, "patch", Alias(documentId, id, "documentId"), Body(patch, value), version);

    [McpServerTool(Name = "delete_document", Title = "Delete document", Destructive = true, OpenWorld = false, UseStructuredContent = true), Description("Delete a document. Requires write access. An omitted version uses the current version as a precondition.")]
    public Task<McpDocumentWrite> DeleteDocument(string databaseId, string collection, string documentId = "", string? version = null, string? organizationId = null, string? projectId = null, string? id = null) =>
        WriteDocumentAsync(Scope(organizationId, projectId), databaseId, collection, "delete", Alias(documentId, id, "documentId"), default, version);

    [McpServerTool(Name = "get_document_history", Title = "Get document history", ReadOnly = true, Destructive = false, OpenWorld = false, Idempotent = true, UseStructuredContent = true), Description("List a document's recorded versions, oldest first, including deletions. Requires a producer history contract.")]
    public Task<McpDocumentHistory> History(string databaseId, string collection, string documentId, string? organizationId = null, string? projectId = null)
    {
        ValidateCollection(Scope(organizationId, projectId), databaseId, collection);
        if (documentId.Length is 0 or > 256 || documentId.Any(char.IsControl)) throw new AccountAccessException("Choose a valid document ID.", 422);
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
        DemandManagement(context);
        if (schema is null) throw new AccountAccessException("Schema must be a JSON object.", 422);
        throw new AccountAccessException("The data plane does not expose editable collection schemas.", 503);
    }

    [McpServerTool(Name = "execute_sql", ReadOnly = true, Destructive = false, OpenWorld = false, Idempotent = true, UseStructuredContent = true), Description("Execute one bounded read-only SQL statement with the same checks as the dashboard editor.")]
    public Task<DatabaseSqlResult> Sql(string databaseId, string sql, string? organizationId = null, string? projectId = null) => data.SqlAsync(Scope(organizationId, projectId), databaseId, sql, Cancellation);

    [McpServerTool(Name = "check_sql", Title = "Check SQL", ReadOnly = true, Destructive = false, OpenWorld = false, Idempotent = true, UseStructuredContent = true), Description("Validate a read-only SQL statement without running it and suggest indexes for it.")]
    public SqlAnalysis CheckSql(string databaseId, string sql, string? organizationId = null, string? projectId = null)
    {
        DatabaseService.Database(Scope(organizationId, projectId), databaseId);
        return SqlValidation.Analyze(sql);
    }

    [McpServerTool(Name = "run_sql", Title = "Run SQL", ReadOnly = true, Destructive = false, OpenWorld = false, Idempotent = true, UseStructuredContent = true), Description("Run one read-only SQL query against a database.")]
    public async Task<McpSqlResponse> RunSql(string databaseId, string sql, string? organizationId = null, string? projectId = null) =>
        new(new((await data.SqlAsync(Scope(organizationId, projectId), databaseId, sql, Cancellation)).Rows));

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
        DemandManagement(context);
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

    [McpServerTool(Name = "list_api_keys", Title = "List API keys", ReadOnly = true, Destructive = false, OpenWorld = false, Idempotent = true, UseStructuredContent = true), Description("List a project's API keys and machine-to-machine (M2M) clients without their secrets.")]
    public async Task<McpCredentialList> ListApiKeys(string kind = "", string status = "", string databaseId = "", string search = "", string? organizationId = null, string? projectId = null)
    {
        if (kind is not ("" or "api-key" or "m2m")) throw new AccountAccessException("Kind must be api-key or m2m.", 422);
        if (status is not ("" or "active" or "inactive")) throw new AccountAccessException("Status must be active or inactive.", 422);
        var context = Scope(organizationId, projectId);
        if (databaseId.Length > 0) DatabaseService.Database(context, databaseId);
        search = search.Trim();
        var list = await keys.ListAsync(context, Cancellation);
        return new(list.Where(key => kind != "m2m" && (status.Length == 0 || (status == "active") == key.IsActive) && (databaseId.Length == 0 || databaseId == key.DatabaseId)
            && (search.Length == 0 || (key.Name + " " + key.Id + "_").Contains(search, StringComparison.OrdinalIgnoreCase))).Select(ApiKeyService.CredentialMetadata).ToArray());
    }

    [McpServerTool(Name = "create_api_key", Title = "Create API key", Destructive = false, OpenWorld = false, UseStructuredContent = true), Description("Issue a project API key for the TerraScale SDKs. Returns the secret once; store it securely. Requires an owner or admin role.")]
    public Task<ProjectApiKeySecret> CreateApiKey(string name, string[] scopes, string databaseId = "", string? organizationId = null, string? projectId = null) =>
        CreateCredentialAsync(Scope(organizationId, projectId), "api-key", name, scopes, databaseId);

    [McpServerTool(Name = "create_m2m_client", Title = "Create M2M client", Destructive = false, OpenWorld = false, UseStructuredContent = true), Description("Issue a machine-to-machine client credential for a project. Requires an owner or admin role and a producer M2M contract.")]
    public Task<ProjectApiKeySecret> CreateM2mClient(string name, string[] scopes, string databaseId = "", string? organizationId = null, string? projectId = null) =>
        CreateCredentialAsync(Scope(organizationId, projectId), "m2m", name, scopes, databaseId);

    [McpServerTool(Name = "rotate_api_key", Title = "Rotate API key", Destructive = true, OpenWorld = false, UseStructuredContent = true), Description("Replace an API key's secret. Requires an owner or admin role and a producer rotation contract.")]
    public async Task<ProjectApiKeySecret> RotateApiKey(string keyId, string? organizationId = null, string? projectId = null)
    {
        var context = Scope(organizationId, projectId);
        DemandManagement(context);
        if (!(await keys.ListAsync(context, Cancellation)).Any(key => key.Id == keyId)) throw new AccountAccessException("Credential is unavailable.", 404);
        throw new AccountAccessException("Credential rotation is unavailable in this dashboard view. Create a replacement key.", 503);
    }

    [McpServerTool(Name = "revoke_api_key", Title = "Revoke API key", Destructive = true, OpenWorld = false, Idempotent = true, UseStructuredContent = true), Description("Revoke a project API key to prevent new token issuance. Previously issued short-lived tokens remain valid until expiry.")]
    public async Task<McpCredentialRevoked> RevokeApiKey(string keyId, string? organizationId = null, string? projectId = null)
    {
        await keys.RevokeAsync(Scope(organizationId, projectId), keyId, Cancellation);
        return new(keyId, true);
    }

    private static McpDatabase DatabaseOutput(DatabaseRecord db) => new(db.Id, db.Name, db.Status, db.Region, DatabaseService.Regions(db), db.CreatedAt);
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
}

public sealed record McpCollectionList(IReadOnlyList<string> Collections);
public sealed record McpDocument(string Id, JsonElement Value, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Version = null);
public sealed record McpDocumentList(IReadOnlyList<McpDocument> Documents);
public sealed record McpDocumentWrite(string Id, string Operation);
public sealed record McpHistoryVersion(string Revision, bool Deleted, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] JsonElement? Value);
public sealed record McpDocumentHistory(IReadOnlyList<McpHistoryVersion> Versions);
public sealed record McpCollectionSchema(string Collection, TerraBaseCollectionSchema Schema);
public sealed record McpSchemaApplied(string Collection, string Status);
public sealed record McpSqlRows(IReadOnlyList<DatabaseSqlRow> Rows);
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

using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using ModelContextProtocol.Server;
using TerraScale.Dashboard.Features.Auth;
using TerraScale.Dashboard.Platform.Mcp;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.Dashboard.Features.Data;

[McpServerToolType]
public sealed class DeclaredSchemaMcpTools(IHttpContextAccessor http, DatabaseService databases, IProjectAccessTokens tokens, IHttpClientFactory factory)
{
    [McpServerTool(Name = "get_collection_validation", Title = "Get collection validation", ReadOnly = true, Destructive = false, OpenWorld = false, Idempotent = true, UseStructuredContent = true), Description("Read declared JSON Schema validation, separate from inferred fields. Schema and dialect are explicitly null when detached; boolean false is an attached schema rejecting future local writes.")]
    public async Task<McpDeclaredValidation> Validation(string databaseId, string collection, string? organizationId = null, string? projectId = null)
    {
        var validation = await DeclaredSchemaAsync(Scope(organizationId, projectId), databaseId, collection, null);
        return new(validation.Schema, validation.Dialect);
    }

    [McpServerTool(Name = "set_collection_schema", Title = "Set collection validation", Destructive = true, OpenWorld = false, Idempotent = true, UseStructuredContent = true), Description("Replace declared Draft 2020-12 validation for future local writes. Existing documents and replicated commits are not revalidated. Requires owner/admin. Does not change inferred fields or unique keys.")]
    public async Task<McpSchemaApplied> SetSchema(string databaseId, string collection,
        [Description("Self-contained JSON Schema object or boolean, at most 64 KiB. True accepts all documents; false rejects all future local writes. Null does not detach validation.")] JsonElement schema,
        string? organizationId = null, string? projectId = null)
    {
        var context = Scope(organizationId, projectId);
        ValidateCollection(context, databaseId, collection);
        McpScope.DemandManagement(context);
        if (schema.ValueKind is not (JsonValueKind.Object or JsonValueKind.True or JsonValueKind.False))
            throw new AccountAccessException("Schema must be a JSON object or boolean.", 422);
        if (System.Text.Encoding.UTF8.GetByteCount(schema.GetRawText()) > 64 * 1024)
            throw new AccountAccessException("Schema must be 64 KiB or less.", 422);
        await DeclaredSchemaAsync(context, databaseId, collection, schema);
        return new(collection, "applied");
    }

    private async Task<TerraBaseDeclaredSchema> DeclaredSchemaAsync(AccountContext context, string databaseId, string collection, JsonElement? schema)
    {
        ValidateCollection(context, databaseId, collection);
        var scope = DatabaseService.Scope(context);
        if (PlatformDatabases.IsPlatform(scope.OrganizationId)) throw PlatformDatabases.ReadOnly();
        var database = DatabaseService.Database(context, databaseId);
        var home = await databases.HomeAsync(context, database, Cancellation);
        using var transport = factory.CreateClient("platform");
        var sdk = new TerraBaseDocumentsClient(transport, new TerraBaseDocumentsClientOptions
        {
            BaseAddress = home.Api,
            Region = home.Id,
            AccessTokenProvider = async cancellationToken => await tokens.GetAsync(scope.Session!, scope.OrganizationId, scope.ProjectId,
                [schema.HasValue ? "documents:write" : "documents:read"], cancellationToken)
        }).Database(scope.ProjectId, database.Name);
        return schema.HasValue ? await sdk.PutDeclaredSchemaAsync(collection, schema.Value, Cancellation)
            : await sdk.GetDeclaredSchemaAsync(collection, Cancellation);
    }

    private static void ValidateCollection(AccountContext context, string databaseId, string collection)
    { DatabaseService.Database(context, databaseId); DataService.ValidCollection(collection); }

    private AccountContext Scope(string? organizationId, string? projectId) => DashboardMcp.Scope(http.HttpContext, organizationId, projectId);
    private CancellationToken Cancellation => http.HttpContext?.RequestAborted ?? default;
}

public sealed record McpDeclaredValidation(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] JsonElement? Schema,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Dialect);

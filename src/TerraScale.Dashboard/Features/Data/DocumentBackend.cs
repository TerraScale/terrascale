using System.Runtime.CompilerServices;
using System.Text.Json;
using TerraScale.Dashboard.Features.Auth;
using TerraScale.Dashboard.Features.Databases;
using TerraScale.DotNet;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.Dashboard.Features.Data;

public interface IDocumentBackend
{
    Task<IReadOnlyList<TerraBaseCollection>> CollectionsAsync(DatabaseScope scope, DatabaseRegion home, string databaseName, CancellationToken cancellationToken);
    Task CreateCollectionAsync(DatabaseScope scope, DatabaseRegion home, string databaseName, string collection, CancellationToken cancellationToken);
    Task DropCollectionAsync(DatabaseScope scope, DatabaseRegion home, string databaseName, string collection, CancellationToken cancellationToken);
    Task<TerraBaseDocumentPage> DocumentsAsync(DatabaseScope scope, DatabaseRegion home, string databaseName, string collection, TerraBaseQuery query, CancellationToken cancellationToken);
    Task<TerraBaseDocumentPage> ListAsync(DatabaseScope scope, DatabaseRegion home, string databaseName, string collection, int limit, string? cursor, CancellationToken cancellationToken);
    Task<TerraBaseDocument?> GetAsync(DatabaseScope scope, DatabaseRegion home, string databaseName, string collection, string id, CancellationToken cancellationToken);
    Task<TerraBaseWriteResult> WriteAsync(DatabaseScope scope, DatabaseRegion home, string databaseName, string collection, string operation, string id, JsonElement document, string version, CancellationToken cancellationToken);
    Task<TerraBaseCollectionSchema> SchemaAsync(DatabaseScope scope, DatabaseRegion home, string databaseName, string collection, CancellationToken cancellationToken);
    IAsyncEnumerable<TerraBaseExportLine> ExportAsync(DatabaseScope scope, DatabaseRegion home, string databaseName, CancellationToken cancellationToken);
}

// Every data request obtains current user/project authority; no customer key is stored.
public sealed class DocumentBackend(IHttpClientFactory http, IProjectAccessTokens tokens) : IDocumentBackend
{
    private readonly TerraScaleIdGenerator ids = new();

    public async Task<IReadOnlyList<TerraBaseCollection>> CollectionsAsync(DatabaseScope scope, DatabaseRegion home, string databaseName, CancellationToken cancellationToken)
    {
        if (PlatformDatabases.IsPlatform(scope.OrganizationId)) return await PlatformDatabases.CollectionsAsync(http.CreateClient("platform"), Session(scope), databaseName, cancellationToken);
        var database = await DatabaseAsync(scope, home, databaseName, "documents:read", cancellationToken);
        var result = new List<TerraBaseCollection>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = null;
        do
        {
            var page = await database.ListCollectionsAsync(200, cursor, cancellationToken);
            result.AddRange(page.Items);
            cursor = page.NextCursor;
            if (!string.IsNullOrEmpty(cursor) && !seen.Add(cursor)) throw new HttpRequestException("Collection pagination did not advance.");
        } while (!string.IsNullOrEmpty(cursor));
        return result;
    }

    public async Task CreateCollectionAsync(DatabaseScope scope, DatabaseRegion home, string databaseName, string collection, CancellationToken cancellationToken) =>
        await (await DatabaseAsync(scope, home, databaseName, "documents:write", cancellationToken)).CreateCollectionAsync(collection, cancellationToken);

    // Drops the collection and every document in it, in one TerraBase transaction.
    public async Task DropCollectionAsync(DatabaseScope scope, DatabaseRegion home, string databaseName, string collection, CancellationToken cancellationToken) =>
        await (await DatabaseAsync(scope, home, databaseName, "documents:write", cancellationToken)).DropCollectionAsync(collection, cancellationToken);

    public async Task<TerraBaseDocumentPage> DocumentsAsync(DatabaseScope scope, DatabaseRegion home, string databaseName, string collection, TerraBaseQuery query, CancellationToken cancellationToken) =>
        PlatformDatabases.IsPlatform(scope.OrganizationId) ? await PlatformDatabases.DocumentsAsync(http.CreateClient("platform"), Session(scope), databaseName, collection, query, cancellationToken) :
        await (await DatabaseAsync(scope, home, databaseName, "documents:read", cancellationToken)).QueryAsync(collection, query, cancellationToken);

    public async Task<TerraBaseDocumentPage> ListAsync(DatabaseScope scope, DatabaseRegion home, string databaseName, string collection, int limit, string? cursor, CancellationToken cancellationToken) =>
        PlatformDatabases.IsPlatform(scope.OrganizationId) ? await PlatformDatabases.ListAsync(http.CreateClient("platform"), Session(scope), databaseName, collection, limit, cursor, cancellationToken) :
        await (await DatabaseAsync(scope, home, databaseName, "documents:read", cancellationToken)).ListDocumentsAsync(collection, limit, cursor, cancellationToken);

    public async Task<TerraBaseDocument?> GetAsync(DatabaseScope scope, DatabaseRegion home, string databaseName, string collection, string id, CancellationToken cancellationToken) =>
        PlatformDatabases.IsPlatform(scope.OrganizationId) ? await PlatformDatabases.GetAsync(http.CreateClient("platform"), Session(scope), databaseName, collection, id, cancellationToken) :
        await (await DatabaseAsync(scope, home, databaseName, "documents:read", cancellationToken)).GetDocumentAsync(collection, id, cancellationToken);

    public async Task<TerraBaseWriteResult> WriteAsync(DatabaseScope scope, DatabaseRegion home, string databaseName, string collection, string operation, string id, JsonElement document, string version, CancellationToken cancellationToken)
    {
        if (operation is not ("create" or "replace" or "patch" or "delete"))
            throw new ArgumentException("Unknown document operation.", nameof(operation));
        var database = await DatabaseAsync(scope, home, databaseName, "documents:write", cancellationToken);
        if (operation == "create" && id.Length == 0) id = ids.NewId();
        var precondition = operation == "create" ? TerraBasePrecondition.Absent : version.Length == 0 ? null : TerraBasePrecondition.Matches(version);
        var attemptId = ids.NewId();
        try
        {
            var result = operation switch
            {
                "create" or "replace" => await database.PutDocumentAsync(collection, id, document, precondition, cancellationToken),
                "patch" => await database.PatchDocumentAsync(collection, id, document, precondition, cancellationToken),
                "delete" => await database.DeleteDocumentAsync(collection, id, precondition, cancellationToken),
                _ => throw new ArgumentException("Unknown document operation.", nameof(operation))
            };
            if (result.Id != id || string.IsNullOrWhiteSpace(result.Version) || result.Offset < 0 || string.IsNullOrWhiteSpace(result.Durability))
                throw new JsonException("The write acknowledgement is incomplete or belongs to another document.");
            return result;
        }
        catch (Exception error) when (error is TimeoutException or IOException or OperationCanceledException or JsonException ||
            error is TerraBaseException { ErrorCode: "invalid_response" } reply && reply.StatusCode is { } status && (int)status is >= 200 and < 300 ||
            error is HttpRequestException request && (request.StatusCode is null || (int)request.StatusCode >= 500))
        {
            // The current producer rejects idempotency keys; replay cannot prove a commit.
            throw new DocumentWriteOutcomeUnknownException(attemptId, id, operation);
        }
    }

    // Every live document from one read snapshot, collection then id order (GET …/export).
    public async IAsyncEnumerable<TerraBaseExportLine> ExportAsync(DatabaseScope scope, DatabaseRegion home, string databaseName, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var database = await DatabaseAsync(scope, home, databaseName, "documents:read", cancellationToken);
        await foreach (var line in database.ExportAsync(cancellationToken: cancellationToken)) yield return line;
    }

    public async Task<TerraBaseCollectionSchema> SchemaAsync(DatabaseScope scope, DatabaseRegion home, string databaseName, string collection, CancellationToken cancellationToken) =>
        await (await DatabaseAsync(scope, home, databaseName, "databases:read", cancellationToken)).GetInferredSchemaAsync(collection, cancellationToken);

    private static SessionRecord Session(DatabaseScope scope) => scope.Session ?? throw new HttpRequestException("Current user authorization is required.");

    private async Task<TerraBaseDatabase> DatabaseAsync(DatabaseScope scope, DatabaseRegion home, string name, string permission, CancellationToken cancellationToken)
    {
        if (PlatformDatabases.IsPlatform(scope.OrganizationId)) throw PlatformDatabases.ReadOnly();
        var session = Session(scope);
        var token = await tokens.GetAsync(session, scope.OrganizationId, scope.ProjectId, [permission], cancellationToken);
        var client = new TerraBaseDocumentsClient(http.CreateClient("platform"), new TerraBaseDocumentsClientOptions
        {
            BaseAddress = home.Api,
            Region = home.Id,
            AccessTokenProvider = _ => ValueTask.FromResult(token)
        });
        return client.Database(scope.ProjectId, name);
    }
}

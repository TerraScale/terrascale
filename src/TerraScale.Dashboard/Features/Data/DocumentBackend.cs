using System.Runtime.CompilerServices;
using System.Text.Json;
using TerraScale.Dashboard.Features.Auth;
using TerraScale.Dashboard.Platform.Rpc;
using TerraScale.TerraBase.Contracts.Changes;
using TerraScale.TerraBase.Contracts.Documents;
using TerraScale.TerraBase.Contracts.Schema;
using TerraScale.TerraBase.Contracts;

namespace TerraScale.Dashboard.Features.Data
{
    public interface IDocumentBackend
    {
        Task<IReadOnlyList<TerraBaseCollectionInfo>> CollectionsAsync(DatabaseScope scope, DatabaseRegion home, string databaseName, CancellationToken cancellationToken);
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
    public sealed class DocumentBackend(TerraBaseRpcClient rpc, IProjectAccessTokens tokens) : IDocumentBackend
    {
        private readonly TerraScaleIdGenerator ids = new();

        public async Task<IReadOnlyList<TerraBaseCollectionInfo>> CollectionsAsync(DatabaseScope scope, DatabaseRegion home, string databaseName, CancellationToken cancellationToken)
        {
            if (PlatformDatabases.IsPlatform(scope.OrganizationId))
            {
                return await PlatformDatabases.CollectionsAsync(rpc, Session(scope), databaseName, cancellationToken);
            }

            var database = await DatabaseAsync(scope, home, databaseName, "documents:read", cancellationToken);
            var result = new List<TerraBaseCollectionInfo>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            string? cursor = null;
            do
            {
                var page = await database.ListCollectionsAsync(200, cursor, cancellationToken);
                result.AddRange(page.Items);
                cursor = page.NextCursor;
                if (!string.IsNullOrEmpty(cursor) && !seen.Add(cursor))
                {
                    throw new HttpRequestException("Collection pagination did not advance.");
                }
            } while (!string.IsNullOrEmpty(cursor));
            return result;
        }

        public async Task CreateCollectionAsync(DatabaseScope scope, DatabaseRegion home, string databaseName, string collection, CancellationToken cancellationToken)
        {
            await (await DatabaseAsync(scope, home, databaseName, "documents:write", cancellationToken)).CreateCollectionAsync(collection, cancellationToken);
        }

        // Drops the collection and every document in it, in one TerraBase transaction.
        public async Task DropCollectionAsync(DatabaseScope scope, DatabaseRegion home, string databaseName, string collection, CancellationToken cancellationToken)
        {
            await (await DatabaseAsync(scope, home, databaseName, "documents:write", cancellationToken)).DropCollectionAsync(collection, cancellationToken);
        }

        public async Task<TerraBaseDocumentPage> DocumentsAsync(DatabaseScope scope, DatabaseRegion home, string databaseName, string collection, TerraBaseQuery query, CancellationToken cancellationToken)
        {
            return PlatformDatabases.IsPlatform(scope.OrganizationId) ? await PlatformDatabases.DocumentsAsync(rpc, Session(scope), databaseName, collection, query, cancellationToken) :
            await (await DatabaseAsync(scope, home, databaseName, "documents:read", cancellationToken)).QueryAsync(collection, query, cancellationToken);
        }

        public async Task<TerraBaseDocumentPage> ListAsync(DatabaseScope scope, DatabaseRegion home, string databaseName, string collection, int limit, string? cursor, CancellationToken cancellationToken)
        {
            return PlatformDatabases.IsPlatform(scope.OrganizationId) ? await PlatformDatabases.ListAsync(rpc, Session(scope), databaseName, collection, limit, cursor, cancellationToken) :
            await (await DatabaseAsync(scope, home, databaseName, "documents:read", cancellationToken)).ListDocumentsAsync(collection, limit, cursor, cancellationToken);
        }

        public async Task<TerraBaseDocument?> GetAsync(DatabaseScope scope, DatabaseRegion home, string databaseName, string collection, string id, CancellationToken cancellationToken)
        {
            return PlatformDatabases.IsPlatform(scope.OrganizationId) ? await PlatformDatabases.GetAsync(rpc, Session(scope), databaseName, collection, id, cancellationToken) :
            await (await DatabaseAsync(scope, home, databaseName, "documents:read", cancellationToken)).GetDocumentAsync(collection, id, cancellationToken);
        }

        public async Task<TerraBaseWriteResult> WriteAsync(DatabaseScope scope, DatabaseRegion home, string databaseName, string collection, string operation, string id, JsonElement document, string version, CancellationToken cancellationToken)
        {
            if (operation is not ("create" or "replace" or "patch" or "delete"))
            {
                throw new ArgumentException("Unknown document operation.", nameof(operation));
            }

            var database = await DatabaseAsync(scope, home, databaseName, "documents:write", cancellationToken);
            if (operation == "create" && id.Length == 0)
            {
                id = ids.NewId();
            }

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
                {
                    throw new JsonException("The write acknowledgement is incomplete or belongs to another document.");
                }

                return result;
            }
            catch (Exception error) when (error is TimeoutException or IOException or OperationCanceledException or JsonException ||
                error is TerraBaseException { ErrorCode: "invalid_response" } reply && reply.Status is >= 200 and < 300 ||
                error is TerraBaseException { Status: >= 500 } ||
                error is HttpRequestException request && (request.StatusCode is null || (int)request.StatusCode >= 500))
            {
                // The current producer rejects idempotency keys; replay cannot prove a commit.
                throw new DocumentWriteOutcomeUnknownException(attemptId, id, operation);
            }
        }

        // Every live document from one read snapshot, collection then id order (GET …/export).
        public async IAsyncEnumerable<TerraBaseExportLine> ExportAsync(DatabaseScope scope, DatabaseRegion home, string databaseName, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var client = await ClientAsync<IChangesService>(scope, home, "documents:read", cancellationToken);
            await foreach (var line in RpcStreams.Read(client.WithCancellationToken(cancellationToken).ExportAsync(scope.ProjectId, databaseName), cancellationToken))
            {
                yield return line;
            }
        }

        public async Task<TerraBaseCollectionSchema> SchemaAsync(DatabaseScope scope, DatabaseRegion home, string databaseName, string collection, CancellationToken cancellationToken)
        {
            var client = await ClientAsync<ISchemaService>(scope, home, "databases:read", cancellationToken);
            return await client.WithCancellationToken(cancellationToken).GetInferredSchemaAsync(scope.ProjectId, databaseName, collection);
        }

        private static SessionRecord Session(DatabaseScope scope)
        {
            return scope.Session ?? throw new HttpRequestException("Current user authorization is required.");
        }

        private async Task<DocumentDatabase> DatabaseAsync(DatabaseScope scope, DatabaseRegion home, string name, string permission, CancellationToken cancellationToken)
        {
            return new(await ClientAsync<IDocumentsService>(scope, home, permission, cancellationToken), scope.ProjectId, name);
        }

        private async Task<T> ClientAsync<T>(DatabaseScope scope, DatabaseRegion home, string permission, CancellationToken cancellationToken)
            where T : MagicOnion.IService<T>
        {
            if (PlatformDatabases.IsPlatform(scope.OrganizationId))
            {
                throw PlatformDatabases.ReadOnly();
            }

            var token = await tokens.GetAsync(Session(scope), scope.OrganizationId, scope.ProjectId, [permission], cancellationToken);
            return rpc.User<T>(token, home.Id);
        }
    }
}

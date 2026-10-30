using System.Text;
using System.Text.Json;
using MagicOnion;
using MagicOnion.Server;
using Microsoft.AspNetCore.Http;
using TerraScale.TerraBase.Contracts;
using TerraScale.TerraBase.Contracts.Documents;

namespace TerraScale.TerraBase.Rpc.Documents
{
    /// <summary>Serves <see cref="IDocumentsService"/> through the Documents REST routes.</summary>
    public sealed class DocumentsService(RestApi api) : ServiceBase<IDocumentsService>, IDocumentsService
    {
        private static readonly TerraBaseContractsJson Json = TerraBaseContractsJson.Default;

        /// <inheritdoc />
        public async UnaryResult<TerraBaseCollectionPage> ListCollectionsAsync(string projectId, string database, int? limit, string? cursor)
        {
            var path = RestPaths.Database(projectId, database) + "/collections" + RestPaths.Query(("limit", RestPaths.Number(limit)), ("cursor", cursor));
            return RestApi.Read(await api.SendAsync(Context, "GET", path), Json.TerraBaseCollectionPage);
        }

        /// <inheritdoc />
        public async UnaryResult<TerraBaseCollectionInfo> CreateCollectionAsync(string projectId, string database, string name)
        {
            var body = Encoding.UTF8.GetBytes("{\"name\":" + JsonSerializer.Serialize(name, Json.String) + "}");
            return RestApi.Read(await api.SendAsync(Context, "POST", RestPaths.Database(projectId, database) + "/collections", body), Json.TerraBaseCollectionInfo);
        }

        /// <inheritdoc />
        public async UnaryResult<TerraBaseCollectionInfo> GetCollectionAsync(string projectId, string database, string name)
        {
            return RestApi.Read(await api.SendAsync(Context, "GET", RestPaths.Collection(projectId, database, name)), Json.TerraBaseCollectionInfo);
        }

        /// <inheritdoc />
        public async UnaryResult<TerraBaseCollectionInfo> DropCollectionAsync(string projectId, string database, string name)
        {
            return RestApi.Read(await api.SendAsync(Context, "DELETE", RestPaths.Collection(projectId, database, name)), Json.TerraBaseCollectionInfo);
        }

        /// <inheritdoc />
        public async UnaryResult<TerraBaseDocument?> GetDocumentAsync(string projectId, string database, string collection, string id)
        {
            var answer = await api.SendAsync(Context, "GET", RestPaths.Document(projectId, database, collection, id));
            return answer.Status == StatusCodes.Status404NotFound && RestApi.FaultOf(answer).Code == "not_found" ? null : RestApi.Read(answer, Json.TerraBaseDocument);
        }

        /// <inheritdoc />
        public async UnaryResult<TerraBaseDocumentPage> ListDocumentsAsync(string projectId, string database, string collection, int? limit, string? cursor)
        {
            var path = RestPaths.Collection(projectId, database, collection) + "/documents" + RestPaths.Query(("limit", RestPaths.Number(limit)), ("cursor", cursor));
            return RestApi.Read(await api.SendAsync(Context, "GET", path), Json.TerraBaseDocumentPage);
        }

        /// <inheritdoc />
        public async UnaryResult<TerraBaseWriteResult> CreateDocumentAsync(string projectId, string database, string collection, JsonElement document)
        {
            var answer = await api.SendAsync(Context, "POST", RestPaths.Collection(projectId, database, collection) + "/documents", RestApi.Json(document, Json.JsonElement));
            return RestApi.Read(answer, Json.TerraBaseWriteResult) with { Created = true };
        }

        /// <inheritdoc />
        public async UnaryResult<TerraBaseWriteResult> PutDocumentAsync(
            string projectId, string database, string collection, string id, JsonElement document, TerraBasePrecondition? precondition)
        {
            var answer = await api.SendAsync(Context, "PUT", RestPaths.Document(projectId, database, collection, id), RestApi.Json(document, Json.JsonElement),
                headers: headers => Apply(headers, precondition));
            return RestApi.Read(answer, Json.TerraBaseWriteResult) with { Created = answer.Status == StatusCodes.Status201Created };
        }

        /// <inheritdoc />
        public async UnaryResult<TerraBaseWriteResult> PatchDocumentAsync(
            string projectId, string database, string collection, string id, JsonElement mergePatch, TerraBasePrecondition? precondition)
        {
            var answer = await api.SendAsync(Context, "PATCH", RestPaths.Document(projectId, database, collection, id), RestApi.Json(mergePatch, Json.JsonElement),
                "application/merge-patch+json", headers => Apply(headers, precondition));
            return RestApi.Read(answer, Json.TerraBaseWriteResult);
        }

        /// <inheritdoc />
        public async UnaryResult<TerraBaseWriteResult> DeleteDocumentAsync(string projectId, string database, string collection, string id, TerraBasePrecondition? precondition)
        {
            var answer = await api.SendAsync(Context, "DELETE", RestPaths.Document(projectId, database, collection, id), headers: headers => Apply(headers, precondition));
            return RestApi.Read(answer, Json.TerraBaseWriteResult);
        }

        /// <inheritdoc />
        public async UnaryResult<TerraBaseDocumentPage> QueryAsync(string projectId, string database, string collection, TerraBaseQuery query)
        {
            var answer = await api.SendAsync(Context, "POST", RestPaths.Collection(projectId, database, collection) + "/query", RestApi.Json(query, Json.TerraBaseQuery));
            return RestApi.Read(answer, Json.TerraBaseDocumentPage);
        }

        /// <inheritdoc />
        public async UnaryResult<TerraBaseTransactionResult> TransactAsync(string projectId, string database, IReadOnlyList<TerraBaseTransactionOperation> operations)
        {
            var answer = await api.SendAsync(Context, "POST", RestPaths.Database(projectId, database) + "/transactions",
                RestApi.Json(new TerraBaseTransaction(operations), Json.TerraBaseTransaction));
            return RestApi.Read(answer, Json.TerraBaseTransactionResult);
        }

        private static void Apply(IHeaderDictionary headers, TerraBasePrecondition? precondition)
        {
            if (precondition?.IfMatch is { } version)
            {
                headers.IfMatch = version == "*" ? "*" : "\"" + version + "\"";
            }

            if (precondition?.IfNoneMatch == true)
            {
                headers.IfNoneMatch = "*";
            }
        }
    }
}

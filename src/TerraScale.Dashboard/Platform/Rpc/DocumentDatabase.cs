using System.Text.Json;
using TerraScale.TerraBase.Contracts.Documents;

namespace TerraScale.Dashboard.Platform.Rpc
{
    // One database of IDocumentsService, bound to its project and name, with cancellation per call.
    public sealed class DocumentDatabase(IDocumentsService service, string projectId, string database)
    {
        public string ProjectId => projectId;

        public string Name => database;

        public async Task<TerraBaseCollectionPage> ListCollectionsAsync(int? limit = null, string? cursor = null, CancellationToken cancellationToken = default)
        {
            return await service.WithCancellationToken(cancellationToken).ListCollectionsAsync(projectId, database, limit, cursor);
        }

        public async Task<TerraBaseCollectionInfo> CreateCollectionAsync(string name, CancellationToken cancellationToken = default)
        {
            return await service.WithCancellationToken(cancellationToken).CreateCollectionAsync(projectId, database, name);
        }

        public async Task<TerraBaseCollectionInfo> GetCollectionAsync(string name, CancellationToken cancellationToken = default)
        {
            return await service.WithCancellationToken(cancellationToken).GetCollectionAsync(projectId, database, name);
        }

        public async Task<TerraBaseCollectionInfo> DropCollectionAsync(string name, CancellationToken cancellationToken = default)
        {
            return await service.WithCancellationToken(cancellationToken).DropCollectionAsync(projectId, database, name);
        }

        public async Task<TerraBaseDocument?> GetDocumentAsync(string collection, string id, CancellationToken cancellationToken = default)
        {
            return await service.WithCancellationToken(cancellationToken).GetDocumentAsync(projectId, database, collection, id);
        }

        public async Task<TerraBaseDocumentPage> ListDocumentsAsync(string collection, int? limit = null, string? cursor = null, CancellationToken cancellationToken = default)
        {
            return await service.WithCancellationToken(cancellationToken).ListDocumentsAsync(projectId, database, collection, limit, cursor);
        }

        public async Task<TerraBaseWriteResult> CreateDocumentAsync(string collection, JsonElement document, CancellationToken cancellationToken = default)
        {
            return await service.WithCancellationToken(cancellationToken).CreateDocumentAsync(projectId, database, collection, document);
        }

        public async Task<TerraBaseWriteResult> PutDocumentAsync(
            string collection, string id, JsonElement document, TerraBasePrecondition? precondition = null, CancellationToken cancellationToken = default)
        {
            return await service.WithCancellationToken(cancellationToken).PutDocumentAsync(projectId, database, collection, id, document, precondition);
        }

        public async Task<TerraBaseWriteResult> PatchDocumentAsync(
            string collection, string id, JsonElement mergePatch, TerraBasePrecondition? precondition = null, CancellationToken cancellationToken = default)
        {
            return await service.WithCancellationToken(cancellationToken).PatchDocumentAsync(projectId, database, collection, id, mergePatch, precondition);
        }

        public async Task<TerraBaseWriteResult> DeleteDocumentAsync(
            string collection, string id, TerraBasePrecondition? precondition = null, CancellationToken cancellationToken = default)
        {
            return await service.WithCancellationToken(cancellationToken).DeleteDocumentAsync(projectId, database, collection, id, precondition);
        }

        public async Task<TerraBaseDocumentPage> QueryAsync(string collection, TerraBaseQuery query, CancellationToken cancellationToken = default)
        {
            return await service.WithCancellationToken(cancellationToken).QueryAsync(projectId, database, collection, query);
        }

        public async Task<TerraBaseTransactionResult> TransactAsync(IReadOnlyList<TerraBaseTransactionOperation> operations, CancellationToken cancellationToken = default)
        {
            return await service.WithCancellationToken(cancellationToken).TransactAsync(projectId, database, operations);
        }
    }
}

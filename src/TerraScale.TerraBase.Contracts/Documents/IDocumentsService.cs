using System.Text.Json;
using MagicOnion;

namespace TerraScale.TerraBase.Contracts.Documents
{
    /// <summary>
    /// The Documents API of one project's databases. Each method is the RPC form of the REST route named in its summary and
    /// answers, authorizes and meters exactly as that route does.
    /// </summary>
    public interface IDocumentsService : IService<IDocumentsService>
    {
        /// <summary><c>GET …/collections</c>.</summary>
        UnaryResult<TerraBaseCollectionPage> ListCollectionsAsync(string projectId, string database, int? limit, string? cursor);

        /// <summary><c>POST …/collections</c>.</summary>
        UnaryResult<TerraBaseCollectionInfo> CreateCollectionAsync(string projectId, string database, string name);

        /// <summary><c>GET …/collections/{collection}</c>.</summary>
        UnaryResult<TerraBaseCollectionInfo> GetCollectionAsync(string projectId, string database, string name);

        /// <summary><c>DELETE …/collections/{collection}</c>.</summary>
        UnaryResult<TerraBaseCollectionInfo> DropCollectionAsync(string projectId, string database, string name);

        /// <summary><c>GET …/documents/by-key</c>; null when the document is absent or deleted.</summary>
        UnaryResult<TerraBaseDocument?> GetDocumentAsync(string projectId, string database, string collection, string id);

        /// <summary><c>GET …/documents</c>: one page in id order.</summary>
        UnaryResult<TerraBaseDocumentPage> ListDocumentsAsync(string projectId, string database, string collection, int? limit, string? cursor);

        /// <summary><c>POST …/documents</c>: creates a document with a TerraBase-generated ULID.</summary>
        UnaryResult<TerraBaseWriteResult> CreateDocumentAsync(string projectId, string database, string collection, JsonElement document);

        /// <summary><c>PUT …/documents/by-key</c>: inserts or replaces a document.</summary>
        UnaryResult<TerraBaseWriteResult> PutDocumentAsync(string projectId, string database, string collection, string id, JsonElement document, TerraBasePrecondition? precondition);

        /// <summary><c>PATCH …/documents/by-key</c> with an RFC 7396 merge patch.</summary>
        UnaryResult<TerraBaseWriteResult> PatchDocumentAsync(string projectId, string database, string collection, string id, JsonElement mergePatch, TerraBasePrecondition? precondition);

        /// <summary><c>DELETE …/documents/by-key</c>.</summary>
        UnaryResult<TerraBaseWriteResult> DeleteDocumentAsync(string projectId, string database, string collection, string id, TerraBasePrecondition? precondition);

        /// <summary><c>POST …/query</c>.</summary>
        UnaryResult<TerraBaseDocumentPage> QueryAsync(string projectId, string database, string collection, TerraBaseQuery query);

        /// <summary><c>POST …/transactions</c>: 1 to 1024 operations, atomically.</summary>
        UnaryResult<TerraBaseTransactionResult> TransactAsync(string projectId, string database, IReadOnlyList<TerraBaseTransactionOperation> operations);
    }
}

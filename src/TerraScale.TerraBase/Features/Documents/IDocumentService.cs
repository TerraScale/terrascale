using TerraScale.TerraBase.Engine;

namespace TerraScale.TerraBase.Features.Documents;

/// <summary>Addresses one document.</summary>
/// <param name="Database">Owning database.</param>
/// <param name="Collection">Collection name (same rules as database names).</param>
/// <param name="Id">Document id: 1 to 256 characters, no control characters.</param>
public sealed record DocumentKey(DatabaseName Database, string Collection, string Id);

/// <summary>JSON document operations for the client API, over <see cref="IStorageEngine"/>.</summary>
public interface IDocumentService
{
    /// <summary>Creates a database; <see langword="false"/> when it already existed.</summary>
    ValueTask<bool> CreateDatabaseAsync(DatabaseName database, CancellationToken cancellationToken);

    /// <summary>Reads a document; <see langword="null"/> when it or its database does not exist.</summary>
    ValueTask<StoredDocument?> GetAsync(DocumentKey key, CancellationToken cancellationToken);

    /// <summary>Stores a JSON object and returns its new version; <see langword="null"/> when the database does not exist.</summary>
    ValueTask<long?> PutAsync(DocumentKey key, ReadOnlyMemory<byte> json, CancellationToken cancellationToken);

    /// <summary>Deletes a document; <see langword="false"/> when it or its database does not exist.</summary>
    ValueTask<bool> DeleteAsync(DocumentKey key, CancellationToken cancellationToken);

    /// <summary>
    /// Lists a page of documents in id order, bounded by <paramref name="limit"/> documents and
    /// <paramref name="maxBytes"/> of JSON; <see langword="null"/> when the database does not exist.
    /// </summary>
    ValueTask<DocumentPage?> ListAsync(
        DatabaseName database, string collection, string? afterId, int limit, long maxBytes, CancellationToken cancellationToken);
}

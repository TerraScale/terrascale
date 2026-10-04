namespace TerraScale.TerraBase.Engine;

/// <summary>A JSON document as stored: UTF-8 JSON text and a per-document version that starts at 1.</summary>
/// <param name="Id">Document id, unique within its collection.</param>
/// <param name="Version">Incremented on every write.</param>
/// <param name="Json">UTF-8 JSON text.</param>
public sealed record StoredDocument(string Id, long Version, ReadOnlyMemory<byte> Json);

/// <summary>One page of documents in id order.</summary>
/// <param name="Documents">The documents of this page.</param>
/// <param name="HasMore">Whether documents after the last one exist.</param>
public sealed record DocumentPage(IReadOnlyList<StoredDocument> Documents, bool HasMore);

/// <summary>Document operations inside one engine transaction.</summary>
public interface IStorageTransaction : IAsyncDisposable
{
    /// <summary>Reads one document, or <see langword="null"/> when absent.</summary>
    ValueTask<StoredDocument?> GetAsync(string collection, string id, CancellationToken cancellationToken);

    /// <summary>Inserts or replaces a document and returns its new version.</summary>
    ValueTask<long> PutAsync(string collection, string id, ReadOnlyMemory<byte> json, CancellationToken cancellationToken);

    /// <summary>Deletes a document; <see langword="false"/> when it did not exist.</summary>
    ValueTask<bool> DeleteAsync(string collection, string id, CancellationToken cancellationToken);

    /// <summary>
    /// Lists documents with ids after <paramref name="afterId"/> in id order: at most
    /// <paramref name="limit"/> documents and, beyond the first, at most <paramref name="maxBytes"/>
    /// of JSON in total. A page cut short by either bound reports <see cref="DocumentPage.HasMore"/>.
    /// </summary>
    ValueTask<DocumentPage> ListAsync(string collection, string? afterId, int limit, long maxBytes, CancellationToken cancellationToken);

    /// <summary>Makes every change of this transaction visible atomically.</summary>
    ValueTask CommitAsync(CancellationToken cancellationToken);
}

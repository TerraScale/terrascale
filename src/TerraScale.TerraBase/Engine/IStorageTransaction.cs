namespace TerraScale.TerraBase.Engine;

/// <summary>
/// A JSON document as stored: UTF-8 JSON text, or a tombstone once deleted, and the database
/// commit offset of the transaction that last wrote it.
/// </summary>
/// <param name="Id">Document id, unique within its collection.</param>
/// <param name="Version">Commit offset of the last write; strictly increases on every write of the document.</param>
/// <param name="Json">UTF-8 JSON text; empty for a tombstone.</param>
/// <param name="Incarnation">Persisted identity of the owning database incarnation.</param>
/// <param name="Deleted">Whether this is a tombstone left by a delete.</param>
public sealed record StoredDocument(string Id, long Version, ReadOnlyMemory<byte> Json, Guid Incarnation, bool Deleted = false);

/// <summary>One page of documents in id order.</summary>
/// <param name="Documents">The documents of this page.</param>
/// <param name="HasMore">Whether documents after the last one exist.</param>
public sealed record DocumentPage(IReadOnlyList<StoredDocument> Documents, bool HasMore);

/// <summary>The version of a document or its tombstone, read without its body.</summary>
/// <param name="Version">Commit offset of the last write.</param>
/// <param name="Deleted">Whether the document is a tombstone.</param>
public readonly record struct DocumentVersion(long Version, bool Deleted);

/// <summary>One page of a snapshot at an offset.</summary>
/// <param name="Rows">Matching live documents in id order.</param>
/// <param name="HasMore">Whether documents after <paramref name="ResumeAfter"/> remain to be examined.</param>
/// <param name="ResumeAfter">
/// Id to continue after: the last document of <paramref name="Rows"/> when the page filled, or the last id examined
/// when the page's work limit stopped it (possibly with no rows); <see langword="null"/> without <paramref name="HasMore"/>.
/// </param>
/// <param name="Expired">
/// Whether the page would have to pass over too many ids first written after the offset: the snapshot is too old to
/// read within bounded work, and the reader must start a new one. Rows are then empty.
/// </param>
public sealed record SnapshotPage(IReadOnlyList<QueryRow> Rows, bool HasMore, string? ResumeAfter, bool Expired = false);

/// <summary>A collection of one database.</summary>
/// <param name="Name">Collection name.</param>
/// <param name="CreatedAt">Creation time in Unix milliseconds.</param>
/// <param name="Generation">
/// Random 64-bit value the engine draws on every creation, so a dropped and recreated collection is a
/// different lineage; set by the engine, ignored on create.
/// </param>
public sealed record CollectionInfo(string Name, long CreatedAt, long Generation = 0);

/// <summary>Document and collection operations inside one engine transaction.</summary>
public interface IStorageTransaction : IAsyncDisposable
{
    /// <summary>Persisted identity of this database incarnation, distinct after deletion/recreation or copying.</summary>
    Guid Incarnation { get; }

    /// <summary>Reads one live document, or <see langword="null"/> when it is absent or deleted.</summary>
    ValueTask<StoredDocument?> GetAsync(string collection, string id, CancellationToken cancellationToken);

    /// <summary>
    /// Reads the version of a document or its tombstone without reading its body; <see langword="null"/> when it was
    /// never written or was removed with <see cref="DeleteAsync"/>.
    /// </summary>
    ValueTask<DocumentVersion?> GetVersionAsync(string collection, string id, CancellationToken cancellationToken);

    /// <summary>
    /// Inserts or replaces a document in an existing collection and returns its new version, the offset of this
    /// transaction. A transaction writes at most 1024 documents of at most 1 MiB with ids of 1 to 1024 UTF-8 bytes,
    /// 7 MiB in total with operation framing, so one commit fits a shape log chunk; a rejected write throws
    /// <see cref="StorageWriteLimitException"/> and the transaction can no longer commit.
    /// </summary>
    ValueTask<long> PutAsync(string collection, string id, ReadOnlyMemory<byte> json, CancellationToken cancellationToken);

    /// <summary>Removes a document or its tombstone without a trace; <see langword="false"/> when there was none.</summary>
    ValueTask<bool> DeleteAsync(string collection, string id, CancellationToken cancellationToken);

    /// <summary>
    /// Replaces a live document with a tombstone that keeps its new version, and returns that version;
    /// <see langword="null"/> when the document is absent or already deleted.
    /// </summary>
    ValueTask<long?> TombstoneAsync(string collection, string id, CancellationToken cancellationToken);

    /// <summary>
    /// Lists live documents with ids after <paramref name="afterId"/> in id order: at most
    /// <paramref name="limit"/> documents and, beyond the first, at most <paramref name="maxBytes"/>
    /// of JSON in total. A page cut short by either bound reports <see cref="DocumentPage.HasMore"/>.
    /// </summary>
    ValueTask<DocumentPage> ListAsync(string collection, string? afterId, int limit, long maxBytes, CancellationToken cancellationToken);

    /// <summary>Runs a query over the live documents of one collection.</summary>
    ValueTask<QueryPage> QueryAsync(DocumentQuery query, CancellationToken cancellationToken);

    /// <summary>
    /// Reads a page of the live documents of one collection as they were at commit <paramref name="offset"/>, in id
    /// order after <see cref="DocumentQuery.After"/>, from the retained change history. Sort keys are ignored. A page
    /// examines a bounded number of ids, so a sparse filter can return a page without rows that still continues. It
    /// ends at <see cref="DocumentQuery.Limit"/> rows, after the row that reaches <see cref="DocumentQuery.MaxBytes"/>
    /// (which may cross it; the caller trims), or at its step limit, without reading on, so it may continue where
    /// nothing follows.
    /// </summary>
    ValueTask<SnapshotPage> SnapshotAsync(DocumentQuery query, long offset, CancellationToken cancellationToken);

    /// <summary>Oldest offset for which a complete snapshot and every later change are retained.</summary>
    ValueTask<long> GetHistoryFloorAsync(CancellationToken cancellationToken);

    /// <summary>The latest committed offset this transaction sees, without committing it.</summary>
    ValueTask<long> GetOffsetAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Streams every retained write and tombstone of the current generation of one collection whose version is in
    /// (<paramref name="after"/>, <paramref name="through"/>], in (version, write) order. A live document that does
    /// not match <paramref name="filter"/> comes back as a tombstone, so it reads as leaving the filtered view.
    /// </summary>
    IAsyncEnumerable<StoredDocument> ChangesAsync(
        string collection, DocumentFilter? filter, long after, long through, CancellationToken cancellationToken);

    /// <summary>
    /// Counts live documents matching <paramref name="filter"/> while reading at most <paramref name="maxExamined"/> + 1
    /// rows (documents and tombstones) in id order. <c>Complete</c> is <see langword="false"/> when the collection holds
    /// more rows, so the count is a lower bound.
    /// </summary>
    ValueTask<(long Count, bool Complete)> CountAsync(string collection, DocumentFilter? filter, long maxExamined, CancellationToken cancellationToken);

    /// <summary>Creates a collection, a commit of its own offset; <see langword="false"/> when it already exists.</summary>
    ValueTask<bool> CreateCollectionAsync(CollectionInfo collection, CancellationToken cancellationToken);

    /// <summary>Reads a collection, or <see langword="null"/> when it does not exist.</summary>
    ValueTask<CollectionInfo?> GetCollectionAsync(string name, CancellationToken cancellationToken);

    /// <summary>Lists at most <paramref name="limit"/> collections with names after <paramref name="afterName"/>, in name order.</summary>
    ValueTask<(IReadOnlyList<CollectionInfo> Collections, bool HasMore)> ListCollectionsAsync(
        string? afterName, int limit, CancellationToken cancellationToken);

    /// <summary>
    /// Drops a collection with every document and tombstone in it, a commit of its own offset; its change history keeps
    /// only tombstones. <see langword="false"/> when it does not exist.
    /// </summary>
    ValueTask<bool> DropCollectionAsync(string name, CancellationToken cancellationToken);

    /// <summary>
    /// Makes every change of this transaction visible atomically and returns the database offset:
    /// this transaction's when it wrote documents or created or dropped a collection, otherwise the latest committed one.
    /// </summary>
    ValueTask<long> CommitAsync(CancellationToken cancellationToken);
}

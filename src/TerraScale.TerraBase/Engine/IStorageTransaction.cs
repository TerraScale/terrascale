using System.Text;

namespace TerraScale.TerraBase.Engine;

/// <summary>
/// A JSON document as stored: UTF-8 JSON text, or a tombstone once deleted, and its merge version.
/// </summary>
/// <param name="Id">Document id, unique within its collection.</param>
/// <param name="Version">Merge version of the last write: its originating database commit offset.</param>
/// <param name="Json">UTF-8 JSON text; empty for a tombstone.</param>
/// <param name="Scope">Identity that scopes the document's ETag: the same on every member serving its database.</param>
/// <param name="Deleted">Whether this is a tombstone left by a delete.</param>
/// <param name="HistoryOffset">Local commit offset when read from shape history; separate from the merge version.</param>
public sealed record StoredDocument(string Id, long Version, ReadOnlyMemory<byte> Json, Guid Scope, bool Deleted = false, long? HistoryOffset = null);

/// <summary>One page of documents in id order.</summary>
/// <param name="Documents">The documents of this page.</param>
/// <param name="HasMore">Whether documents after the last one exist.</param>
public sealed record DocumentPage(IReadOnlyList<StoredDocument> Documents, bool HasMore);

/// <summary>The version of a document or its tombstone, read without its body.</summary>
/// <param name="Version">Merge version of the last write.</param>
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

/// <summary>One replicated document write: a JSON object, or a deletion, at the version it was written with.</summary>
/// <param name="Collection">Collection name.</param>
/// <param name="Id">Document id.</param>
/// <param name="Version">Version written; a deletion carries the version of its tombstone.</param>
/// <param name="Json">UTF-8 JSON text, or <see langword="null"/> for a deletion.</param>
public sealed record DocumentChange(string Collection, string Id, long Version, ReadOnlyMemory<byte>? Json)
{
    /// <summary>
    /// Reserved collection of a client database's collections: one document per collection, a tombstone once dropped, so
    /// creations and drops replicate and merge like documents. No client collection can have the name.
    /// </summary>
    internal const string Collections = "_collections";

    /// <summary>Reserved collection of a client database's secondary indexes: one definition per index, a tombstone once dropped.</summary>
    internal const string Indexes = "_indexes";

    private static readonly UTF8Encoding Utf8 = new(false, true);

    internal static bool HasValidKey(string collection, string id)
    {
        if (!(DatabaseName.TryParse(collection, out _) || collection is Collections or Indexes) || id.Length is 0 or > 256 || id.Any(char.IsControl))
        {
            return false;
        }

        try
        {
            _ = Utf8.GetByteCount(id);
            return true;
        }
        catch (EncoderFallbackException)
        {
            return false;
        }
    }
}

/// <summary>Document and collection operations inside one engine transaction.</summary>
/// <remarks>
/// Local document mutation calls (<see cref="PutAsync"/>, <see cref="DeleteAsync"/>, <see cref="TombstoneAsync"/>
/// and <see cref="DropCollectionAsync"/>) cannot share a transaction with <see cref="ApplyAsync"/>, in either order,
/// even when a call changes nothing. Mixing them throws and makes the transaction rollback-only.
/// Collection creation and applied-frontier updates remain available to replicated transactions.
/// </remarks>
public interface IStorageTransaction : IAsyncDisposable
{
    /// <summary>Most document changes one captured write transaction may commit.</summary>
    const int MaxChangeGroupChanges = 1024;

    /// <summary>Most UTF-8 bytes of collections, ids and JSON one captured write transaction may commit.</summary>
    const int MaxChangeGroupBytes = 2 * 1024 * 1024;

    /// <summary>
    /// Identity that scopes document ETags: derived from a client database's organization, project and database ids, so
    /// it is the same on every member and distinct after deletion/recreation or copying; the system database's incarnation.
    /// </summary>
    Guid Scope { get; }

    /// <summary>Reads one live document, or <see langword="null"/> when it is absent or deleted.</summary>
    ValueTask<StoredDocument?> GetAsync(string collection, string id, CancellationToken cancellationToken);

    /// <summary>
    /// Reads the version of a document or its tombstone without reading its body; <see langword="null"/> when it was
    /// never written or was removed with <see cref="DeleteAsync"/>.
    /// </summary>
    ValueTask<DocumentVersion?> GetVersionAsync(string collection, string id, CancellationToken cancellationToken);

    /// <summary>
    /// Inserts or replaces a document in an existing collection and returns its new version, the offset of this
    /// transaction. A transaction writes at most 1024 documents of at most 1 MiB. Captured ids have 1 to 256
    /// characters, no control characters, and valid UTF-8; collection names satisfy <see cref="DatabaseName"/>.
    /// Total writes and operation framing fit at most 7 MiB, so one commit fits a shape log chunk.
    /// Invalid keys throw <see cref="InvalidDataException"/>; exceeded write budgets throw
    /// <see cref="StorageWriteLimitException"/>. A rejected write prevents the transaction from committing.
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
    /// Streams every retained write and tombstone of the current generation of one collection whose local history offset is in
    /// (<paramref name="after"/>, <paramref name="through"/>], in (history offset, write) order. A live document that does
    /// not match <paramref name="filter"/> comes back as a tombstone, so it reads as leaving the filtered view.
    /// </summary>
    /// <remarks>
    /// Enumeration exclusively owns the transaction. Other operations, another stream, and transaction disposal
    /// fail with <see cref="InvalidOperationException"/> until the enumerator completes or is disposed.
    /// A write refused during enumeration still makes the transaction rollback-only.
    /// </remarks>
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
    /// Counts the live documents of one collection (0 when it holds none, or was never created), without reading other
    /// collections or any body.
    /// </summary>
    ValueTask<long> CountDocumentsAsync(string collection, CancellationToken cancellationToken);

    /// <summary>Counts the live documents of every collection, in name order, without reading document bodies.</summary>
    ValueTask<IReadOnlyList<CollectionCount>> CountCollectionsAsync(CancellationToken cancellationToken);

    /// <summary>Lists every collection, in name order, with its live documents and their JSON size (read from size metadata).</summary>
    ValueTask<IReadOnlyList<CollectionStats>> ListCollectionStatsAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Streams the live documents <paramref name="query"/> selects, at most <see cref="SqlQuery.Limit"/>, one at a time:
    /// the caller bounds what it keeps and stops reading when it has enough. The query work budget of
    /// <see cref="QueryAsync"/> applies: past it the stream fails with a <see cref="QueryCapacityException"/>, and cancelling
    /// <paramref name="cancellationToken"/> stops the query even while the engine is still scanning or sorting, with an
    /// <see cref="OperationCanceledException"/>.
    /// </summary>
    IAsyncEnumerable<StoredDocument> QuerySqlAsync(SqlQuery query, CancellationToken cancellationToken);

    /// <summary>Describes how the engine would run <paramref name="query"/>, without running it.</summary>
    ValueTask<SqlQueryPlan> ExplainSqlAsync(SqlQuery query, CancellationToken cancellationToken);

    /// <summary>
    /// Drops every collection whose name starts with <paramref name="prefix"/>, with every document and tombstone in
    /// them; returns how many documents and tombstones were removed.
    /// </summary>
    ValueTask<long> DeleteCollectionsAsync(string prefix, CancellationToken cancellationToken);

    /// <summary>Lists the secondary indexes of this database in name order.</summary>
    ValueTask<IReadOnlyList<IndexDefinition>> ListIndexesAsync(CancellationToken cancellationToken);

    /// <summary>Builds a secondary index over live documents; <see langword="false"/> when an index with the name already exists.</summary>
    ValueTask<bool> CreateIndexAsync(IndexDefinition index, CancellationToken cancellationToken);

    /// <summary>Drops a secondary index; <see langword="false"/> when it did not exist.</summary>
    ValueTask<bool> DropIndexAsync(string name, CancellationToken cancellationToken);

    /// <summary>
    /// Makes every change of this transaction visible atomically and returns the database offset:
    /// this transaction's when it wrote documents or created or dropped a collection, otherwise the latest committed one.
    /// </summary>
    ValueTask<long> CommitAsync(CancellationToken cancellationToken);
    /// <summary>
    /// The origin of this database: 16 random bytes created with it. Captured change groups are numbered
    /// 1, 2, … under it.
    /// </summary>
    ValueTask<ReadOnlyMemory<byte>> GetOriginAsync(CancellationToken cancellationToken);

    /// <summary>The last locally committed change group sequence, or 0 for an empty journal.</summary>
    ValueTask<long> GetLastChangeSequenceAsync(CancellationToken cancellationToken);

    /// <summary>Reads captured change group <paramref name="sequence"/>, or <see langword="null"/> when it does not exist.</summary>
    ValueTask<IReadOnlyList<DocumentChange>?> ReadChangesAsync(long sequence, CancellationToken cancellationToken);

    /// <summary>
    /// Reads the SHA-256 identity of the captured prefix through <paramref name="sequence"/>, or null for a missing group.
    /// Sequence 0 returns <see cref="JournalDigest.Initial"/> for this database's origin.
    /// </summary>
    ValueTask<ReadOnlyMemory<byte>?> GetChangeDigestAsync(long sequence, CancellationToken cancellationToken);

    /// <summary>
    /// Merges a replicated change; it is never captured. The greater state wins, ordered by version, then a
    /// deletion over a live document, then the UTF-8 JSON bytes, so every replica that applies the same
    /// changes in any order ends in the same state. Returns whether the stored state changed.
    /// </summary>
    ValueTask<bool> ApplyAsync(DocumentChange change, CancellationToken cancellationToken);

    /// <summary>The last change group of <paramref name="origin"/> applied to this database; 0 when none.</summary>
    ValueTask<long> GetAppliedSequenceAsync(ReadOnlyMemory<byte> origin, CancellationToken cancellationToken);

    /// <summary>
    /// The atomically stored sequence and SHA-256 prefix identity of <paramref name="origin"/>. A missing or legacy
    /// unproven frontier returns sequence 0 and the empty-journal digest; it cannot acknowledge old groups.
    /// </summary>
    ValueTask<(long Sequence, ReadOnlyMemory<byte> Digest)> GetAppliedFrontierAsync(ReadOnlyMemory<byte> origin, CancellationToken cancellationToken);

    /// <summary>
    /// Records an unproven legacy sequence, which replication will not trust. Use <see cref="SetAppliedFrontierAsync"/>
    /// to record a received group together with its validated prefix identity.
    /// </summary>
    ValueTask SetAppliedSequenceAsync(ReadOnlyMemory<byte> origin, long sequence, CancellationToken cancellationToken);

    /// <summary>Records a received sequence and its validated SHA-256 prefix identity in the document transaction.</summary>
    ValueTask SetAppliedFrontierAsync(ReadOnlyMemory<byte> origin, long sequence, ReadOnlyMemory<byte> digest, CancellationToken cancellationToken);
}

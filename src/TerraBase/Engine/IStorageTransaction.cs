using System.Text;
using System.Text.Json;
using TerraBase.Engine.Lattices;

namespace TerraBase.Engine;

/// <summary>
/// A JSON document as stored: UTF-8 JSON text, or a tombstone once deleted, and its version.
/// </summary>
/// <param name="Id">Document id, unique within its collection.</param>
/// <param name="Version">
/// In a client database, the stamp of the last-writer-wins write of its register (an <see cref="Hlc"/>); in the system
/// database, the commit offset of its last write.
/// </param>
/// <param name="Json">UTF-8 JSON text; empty for a tombstone.</param>
/// <param name="Scope">Identity that scopes the document's ETag: the same on every member serving its database.</param>
/// <param name="Deleted">Whether this is a tombstone left by a delete.</param>
/// <param name="HistoryOffset">Local commit offset when read from shape history; separate from the merge version.</param>
/// <param name="UniqueConflict">The retained state lost a replicated uniqueness conflict and is excluded from queries.</param>
public sealed record StoredDocument(string Id, long Version, ReadOnlyMemory<byte> Json, Guid Scope, bool Deleted = false, long? HistoryOffset = null, bool UniqueConflict = false);

/// <summary>One page of documents in id order.</summary>
/// <param name="Documents">The documents of this page.</param>
/// <param name="HasMore">Whether documents after the last one exist.</param>
public sealed record DocumentPage(IReadOnlyList<StoredDocument> Documents, bool HasMore);

/// <summary>One bounded page of stored document registers, live or tombstone, in collection and id order.</summary>
/// <param name="States">The registers in this page, including collection, index and uniqueness metadata.</param>
/// <param name="HasMore">Whether keys after the returned cursor remain.</param>
/// <param name="AfterCollection">Last examined collection; null before any key.</param>
/// <param name="AfterId">Last examined id; null before any key.</param>
/// <param name="Unmaterialized">
/// The states of <paramref name="States"/> whose row does not read as the register's last-writer-wins write (damage to its
/// version or body alone); applying such a state rewrites its row.
/// </param>
public sealed record ReplicationStatePage(
    IReadOnlyList<DocumentDelta> States, bool HasMore, string? AfterCollection, string? AfterId, IReadOnlyList<DocumentDelta> Unmaterialized);

/// <summary>The version of a document or its tombstone, read without its body.</summary>
/// <param name="Version">The version of <see cref="StoredDocument"/>.</param>
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
/// <param name="Id">Canonical uppercase ULID of this collection creation.</param>
/// <param name="Generation">
/// Random 64-bit value the engine draws on every creation, so a dropped and recreated collection is a
/// different lineage; set by the engine, ignored on create.
/// </param>
public sealed record CollectionInfo(string Name, long CreatedAt, long Generation = 0, string Id = "");

/// <summary>
/// One write of the system database, which its catalog followers replay in order (docs/decisions/0009): a JSON value, or a
/// deletion, at the commit offset it was written at.
/// </summary>
/// <param name="Collection">Collection name.</param>
/// <param name="Id">Document id.</param>
/// <param name="Version">Commit offset written; a deletion carries the offset that removed the document.</param>
/// <param name="Json">UTF-8 JSON text, or <see langword="null"/> for a deletion.</param>
// TS-647 step 5 makes the system database a lattice database too, and this goes with the catalog primary.
public sealed record DocumentChange(string Collection, string Id, long Version, ReadOnlyMemory<byte>? Json);

/// <summary>
/// A replicated change of one client document (docs/design/anna-core.md, Documents): a delta of its register, as a local
/// write captured it, or its whole state, as a snapshot sends it. Applying joins it into the stored register.
/// </summary>
/// <param name="Collection">Collection name.</param>
/// <param name="Id">Document id.</param>
/// <param name="State">The register delta or state.</param>
public sealed record DocumentDelta(string Collection, string Id, MvRegister<DocumentWrite> State)
{
    /// <summary>
    /// Reserved collection of a client database's collections: one document per collection, a tombstone once dropped, so
    /// creations and drops replicate and join like documents. No client collection can have the name.
    /// </summary>
    internal const string Collections = "_collections";

    /// <summary>Reserved collection of a client database's secondary indexes: one definition per index, a tombstone once dropped.</summary>
    internal const string Indexes = "_indexes";

    internal const string Unique = "_unique";

    private static readonly UTF8Encoding Utf8 = new(false, true);

    internal static bool HasValidKey(string collection, string id)
    {
        if (!(DatabaseName.TryParse(collection, out _) || collection is Collections or Indexes or Unique) || id.Length is 0 or > 256 || id.Any(char.IsControl))
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

/// <summary>A position in an origin's stream: the 16-byte origin and the last of its groups applied.</summary>
/// <param name="Origin">The origin.</param>
/// <param name="Sequence">The group, 1 or more.</param>
public sealed record OriginPosition(ReadOnlyMemory<byte> Origin, long Sequence);

/// <summary>
/// One change group a client database captured (docs/design/anna-core.md, Per-origin streams): the deltas of one commit,
/// at most one per document, and its dependency vector: the applied position of every other origin it had applied when
/// it committed.
/// </summary>
/// <param name="Dependencies">One position per origin, in origin byte order.</param>
/// <param name="Changes">The deltas, in the order the commit first wrote their documents.</param>
public sealed record CapturedGroup(IReadOnlyList<OriginPosition> Dependencies, IReadOnlyList<DocumentDelta> Changes);

/// <summary>Document and collection operations inside one engine transaction.</summary>
/// <remarks>
/// Local document mutation calls (<see cref="PutAsync"/>, <see cref="DeleteAsync"/>, <see cref="TombstoneAsync"/>,
/// <see cref="SetCollectionSchemaAsync"/> and <see cref="DropCollectionAsync"/>) cannot share a transaction with <see cref="ApplyAsync"/>, in either order,
/// even when a call changes nothing. Mixing them throws and makes the transaction rollback-only.
/// Collection creation and applied-position updates remain available to replicated transactions.
/// </remarks>
public interface IStorageTransaction : IAsyncDisposable
{
    /// <summary>Most document changes one captured write transaction may commit.</summary>
    const int MaxChangeGroupChanges = 1024;

    /// <summary>
    /// Most bytes of collections, ids and encoded register states one captured write transaction may commit. A group of
    /// one change may hold more: a snapshot sends a register with concurrent values whole.
    /// </summary>
    const int MaxChangeGroupBytes = 2 * 1024 * 1024;

    /// <summary>Most bytes of collection, id and encoded register state a client change group of one change may hold.</summary>
    const int MaxSingleChangeBytes = 3 * 1024 * 1024;

    /// <summary>
    /// Identity that scopes document ETags: derived from a client database's organization, project and database ids, so
    /// it is the same on every member and distinct after deletion/recreation or copying; the system database's incarnation.
    /// </summary>
    Guid Scope { get; }

    /// <summary>Reads one live document, or <see langword="null"/> when it is absent or deleted.</summary>
    ValueTask<StoredDocument?> GetAsync(string collection, string id, CancellationToken cancellationToken);

    /// <summary>Reads retained current JSON byte length, including hidden uniqueness conflicts, without materializing the document; missing documents and tombstones have zero bytes.</summary>
    ValueTask<long> GetDocumentBytesAsync(string collection, string id, CancellationToken cancellationToken);

    /// <summary>
    /// Totals retained current document JSON, including hidden uniqueness conflicts, in this database's collections without materializing bodies or collection statistics.
    /// Trusted storage accounting bypasses request query budgets; cancellation still stops the read.
    /// History, tombstones and internal metadata outside collections are excluded.
    /// </summary>
    ValueTask<long> GetStoredDocumentBytesAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Reads the version of a document or its tombstone without reading its body; <see langword="null"/> when it was
    /// never written, or was removed from the system database with <see cref="DeleteAsync"/>.
    /// </summary>
    ValueTask<DocumentVersion?> GetVersionAsync(string collection, string id, CancellationToken cancellationToken);

    /// <summary>
    /// Inserts or replaces a document in an existing collection and returns its new version: in a client database the
    /// stamp of this write, which replaces every value of the document's register this member knows; in the system
    /// database the offset of this transaction. A transaction writes at most 1024 documents of at most 1 MiB. Captured ids have 1 to 256
    /// characters, no control characters, and valid UTF-8; collection names satisfy <see cref="DatabaseName"/>.
    /// Total writes and operation framing fit at most 7 MiB, so one commit fits a shape log chunk.
    /// Invalid keys throw <see cref="InvalidDataException"/>; exceeded write budgets throw
    /// <see cref="StorageWriteLimitException"/>. A rejected write prevents the transaction from committing.
    /// </summary>
    ValueTask<long> PutAsync(string collection, string id, ReadOnlyMemory<byte> json, CancellationToken cancellationToken);

    /// <summary>
    /// Runs <see cref="GetVersionAsync"/> then <see cref="PutAsync"/> for each of <paramref name="puts"/> in order, under
    /// the same limits and errors, and fills <paramref name="results"/> (at least as long) with each new version and
    /// the state it replaced. An entry is filled only once its put succeeded: when a put throws, every entry from its
    /// index on keeps version 0, and the puts before it remain written (a rejected write prevents committing anyway).
    /// </summary>
    /// <remarks>Engines override this to run the whole run in one call; the default makes two calls per put.</remarks>
    async ValueTask PutBatchAsync(
        IReadOnlyList<(string Collection, string Id, ReadOnlyMemory<byte> Json)> puts, Memory<(long Version, DocumentVersion? Previous)> results,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(puts);
        ArgumentOutOfRangeException.ThrowIfLessThan(results.Length, puts.Count);
        results.Span[..puts.Count].Clear();
        for (var i = 0; i < puts.Count; i++)
        {
            var (collection, id, json) = puts[i];
            var previous = await GetVersionAsync(collection, id, cancellationToken).ConfigureAwait(false);
            var version = await PutAsync(collection, id, json, cancellationToken).ConfigureAwait(false);
            results.Span[i] = (version, previous);
        }
    }

    /// <summary>
    /// Removes a system database document without a trace; <see langword="false"/> when there was none. A client database
    /// refuses it with <see cref="NotSupportedException"/>: its deletions are tombstones, which replicate.
    /// </summary>
    ValueTask<bool> DeleteAsync(string collection, string id, CancellationToken cancellationToken);

    /// <summary>
    /// Replaces a live document with a tombstone that keeps its new version (as <see cref="PutAsync"/>), and returns that
    /// version; <see langword="null"/> when the document is absent or already deleted.
    /// </summary>
    ValueTask<long?> TombstoneAsync(string collection, string id, CancellationToken cancellationToken);

    /// <summary>
    /// Lists live documents with ids after <paramref name="afterId"/> in id order: at most
    /// <paramref name="limit"/> documents and, beyond the first, at most <paramref name="maxBytes"/>
    /// of JSON in total. A page cut short by either bound reports <see cref="DocumentPage.HasMore"/>.
    /// </summary>
    ValueTask<DocumentPage> ListAsync(string collection, string? afterId, int limit, long maxBytes, CancellationToken cancellationToken);

    /// <summary>Runs a query over the live documents of one collection.</summary>
    /// <remarks><paramref name="observation"/> accumulates candidate visits, including sort admission, even when execution fails.</remarks>
    ValueTask<QueryPage> QueryAsync(DocumentQuery query, CancellationToken cancellationToken, QueryObservation? observation = null);

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
    /// Reads every retained write and tombstone of the current collection generation, including repeated writes,
    /// in local (history offset, append sequence) order strictly after
    /// (<paramref name="afterOffset"/>, <paramref name="afterSequence"/>). Both cursor parts are nonnegative;
    /// resume with the last returned change's offset and sequence. This order is local to this database instance.
    /// </summary>
    /// <remarks>
    /// <paramref name="limit"/> is 1 to 1000 and <paramref name="maxBytes"/> is 2 to 8 MiB, inclusive.
    /// The page stays within both bounds, charging each row 2048 framing bytes plus its UTF-8 id and body bytes.
    /// Sizes are checked before materializing ids or bodies; an oversized first row throws
    /// <see cref="InvalidDataException"/>. Live JSON is validated without rewriting its stored bytes.
    /// The caller reserves memory and validates database scope, incarnation, collection generation, head and
    /// history floor. No history is pruned. A missing collection returns an empty page.
    /// </remarks>
    ValueTask<CollectionChangePage> ReadCollectionChangesAsync(
        string collection, long afterOffset, long afterSequence, int limit, long maxBytes, CancellationToken cancellationToken);

    /// <summary>
    /// Counts live documents matching <paramref name="filter"/> while reading at most <paramref name="maxExamined"/> + 1
    /// rows (documents and tombstones) in id order. <c>Complete</c> is <see langword="false"/> when the collection holds
    /// more rows, so the count is a lower bound.
    /// </summary>
    /// <remarks><paramref name="observation"/> includes repeated visits in the separate overflow walk.</remarks>
    ValueTask<(long Count, bool Complete)> CountAsync(string collection, DocumentFilter? filter, long maxExamined, CancellationToken cancellationToken, QueryObservation? observation = null);

    /// <summary>Whether a collection (or name prefix) has documents, tombstones, collection metadata or retained replication deletions or shape history.</summary>
    ValueTask<bool> HasCollectionStateAsync(string name, bool prefix, CancellationToken cancellationToken);

    /// <summary>Creates a collection, a commit of its own offset; <see langword="false"/> when it already exists.</summary>
    ValueTask<bool> CreateCollectionAsync(CollectionInfo collection, CancellationToken cancellationToken);

    /// <summary>Reads a collection, or <see langword="null"/> when it does not exist.</summary>
    ValueTask<CollectionInfo?> GetCollectionAsync(string name, CancellationToken cancellationToken);

    /// <summary>Reads a collection's attached schema, or <see langword="null"/> when none is attached or the collection is absent.</summary>
    ValueTask<JsonElement?> GetCollectionSchemaAsync(string name, CancellationToken cancellationToken);

    /// <summary>
    /// Attaches a schema of at most 64 KiB of UTF-8 JSON, or detaches it with <see langword="null"/>;
    /// <see langword="false"/> when the collection is absent. Preserves collection creation time and generation.
    /// </summary>
    ValueTask<bool> SetCollectionSchemaAsync(string name, JsonElement? schema, CancellationToken cancellationToken);

    /// <summary>Lists at most <paramref name="limit"/> collections with names after <paramref name="afterName"/>, in name order.</summary>
    ValueTask<(IReadOnlyList<CollectionInfo> Collections, bool HasMore)> ListCollectionsAsync(
        string? afterName, int limit, CancellationToken cancellationToken);

    /// <summary>
    /// Drops a collection, a commit of its own offset; its change history keeps only tombstones. A client database
    /// writes a tombstone over every document of it, which replicate; the system database removes them without a trace.
    /// <see langword="false"/> when it does not exist.
    /// </summary>
    ValueTask<bool> DropCollectionAsync(string name, CancellationToken cancellationToken);

    /// <summary>
    /// Counts the live documents of one collection (0 when it holds none, or was never created), without reading other
    /// collections or any body.
    /// </summary>
    ValueTask<long> CountDocumentsAsync(string collection, CancellationToken cancellationToken);

    /// <summary>Lists every collection, in name order, with its live documents and their JSON size (read from size metadata).</summary>
    ValueTask<IReadOnlyList<CollectionStats>> ListCollectionStatsAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Streams the live documents <paramref name="query"/> selects, at most <see cref="SqlQuery.Limit"/>, one at a time:
    /// the caller bounds what it keeps and stops reading when it has enough. The query work budget of
    /// <see cref="QueryAsync"/> applies: past it the stream fails with a <see cref="QueryCapacityException"/>, and cancelling
    /// <paramref name="cancellationToken"/> stops the query even while the engine is still scanning or sorting, with an
    /// <see cref="OperationCanceledException"/>.
    /// </summary>
    /// <remarks><paramref name="observation"/> retains candidate visits when the stream is cancelled or disposed early.</remarks>
    IAsyncEnumerable<StoredDocument> QuerySqlAsync(SqlQuery query, CancellationToken cancellationToken, QueryObservation? observation = null);

    /// <summary>Describes how the engine would run <paramref name="query"/>, without running it.</summary>
    ValueTask<SqlQueryPlan> ExplainSqlAsync(SqlQuery query, CancellationToken cancellationToken);

    /// <summary>
    /// Drops every system database collection whose name starts with <paramref name="prefix"/>, with every document in
    /// them; returns how many documents were removed. A client database refuses it with <see cref="NotSupportedException"/>.
    /// </summary>
    ValueTask<long> DeleteCollectionsAsync(string prefix, CancellationToken cancellationToken);

    /// <summary>Lists the secondary indexes of this database in name order.</summary>
    ValueTask<IReadOnlyList<IndexDefinition>> ListIndexesAsync(CancellationToken cancellationToken);

    /// <summary>Builds a secondary index over live documents; <see langword="false"/> when an index with the name already exists.</summary>
    ValueTask<bool> CreateIndexAsync(IndexDefinition index, CancellationToken cancellationToken);

    /// <summary>Drops a secondary index; <see langword="false"/> when it did not exist.</summary>
    ValueTask<bool> DropIndexAsync(string name, CancellationToken cancellationToken);

    /// <summary>Reads the unique keys declared for a collection.</summary>
    ValueTask<IReadOnlyList<UniqueConstraint>> GetUniqueConstraintsAsync(string collection, CancellationToken cancellationToken);

    /// <summary>Declares unique keys for an empty collection; an unchanged declaration is idempotent.</summary>
    ValueTask SetUniqueConstraintsAsync(string collection, IReadOnlyList<UniqueConstraint> constraints, CancellationToken cancellationToken);

    /// <summary>
    /// Makes every change of this transaction visible atomically and returns the database offset:
    /// this transaction's when it wrote documents or created or dropped a collection, otherwise the latest committed one.
    /// </summary>
    ValueTask<long> CommitAsync(CancellationToken cancellationToken);

    /// <summary>
    /// The origin of this database's own stream, 16 bytes. In a client database: this member's replica id
    /// (little-endian) and the first 8 bytes of the file's incarnation, so a new epoch or a copied file starts a new
    /// stream (docs/design/anna-core.md, Per-origin streams). In the system database: its incarnation. Captured change
    /// groups are numbered 1, 2, … under it.
    /// </summary>
    ValueTask<ReadOnlyMemory<byte>> GetOriginAsync(CancellationToken cancellationToken);

    /// <summary>The last locally committed change group sequence, or 0 for an empty journal.</summary>
    ValueTask<long> GetLastChangeSequenceAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Reads captured change group <paramref name="sequence"/> of a client database, or <see langword="null"/> when it does
    /// not exist. The system database's groups are read through <see cref="ISystemReplication"/>.
    /// </summary>
    ValueTask<CapturedGroup?> ReadChangesAsync(long sequence, CancellationToken cancellationToken);

    /// <summary>
    /// Reads the stored register of every document, live or tombstone, including collection, index and uniqueness
    /// metadata, after the (<paramref name="afterCollection"/>, <paramref name="afterId"/>) cursor in collection and id
    /// order. Both cursor parts are null for the first page. Each page examines at most 128 keys and holds at most 128
    /// registers: the first of at most <see cref="MaxSingleChangeBytes"/> and all of them of at most
    /// <see cref="MaxChangeGroupBytes"/> bytes of keys and encoded states, as <see cref="ApplyAsync"/> admits them.
    /// Sizes are checked before a register is read. Reuse one transaction for every page to read a consistent state.
    /// </summary>
    ValueTask<ReplicationStatePage> ReadReplicationStateAsync(string? afterCollection, string? afterId, CancellationToken cancellationToken);

    /// <summary>
    /// Joins a replicated delta into the stored register of its document; it is never captured. Joins are associative,
    /// commutative and idempotent, so every replica that applies the same deltas in any order, any number of times, ends
    /// in the same state. Returns whether the stored state changed. A delta whose dot carries another value than the
    /// stored one (a replica minted it twice) is refused with <see cref="InvalidDataException"/>.
    /// </summary>
    ValueTask<bool> ApplyAsync(DocumentDelta change, CancellationToken cancellationToken);

    /// <summary>The last change group of <paramref name="origin"/> applied to this database; 0 when none.</summary>
    ValueTask<long> GetAppliedSequenceAsync(ReadOnlyMemory<byte> origin, CancellationToken cancellationToken);

    /// <summary>Records that the change groups of <paramref name="origin"/> through <paramref name="sequence"/> are applied.</summary>
    ValueTask SetAppliedSequenceAsync(ReadOnlyMemory<byte> origin, long sequence, CancellationToken cancellationToken);
}

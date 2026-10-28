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
/// <param name="UniqueConflict">The retained state lost a replicated uniqueness conflict and is excluded from queries.</param>
public sealed record StoredDocument(string Id, long Version, ReadOnlyMemory<byte> Json, Guid Scope, bool Deleted = false, bool UniqueConflict = false);

/// <summary>One page of documents in id order.</summary>
/// <param name="Documents">The documents of this page.</param>
/// <param name="HasMore">Whether documents after the last one exist.</param>
public sealed record DocumentPage(IReadOnlyList<StoredDocument> Documents, bool HasMore);

/// <summary>The version of a document or its tombstone, read without its body.</summary>
/// <param name="Version">The version of <see cref="StoredDocument"/>.</param>
/// <param name="Deleted">Whether the document is a tombstone.</param>
public readonly record struct DocumentVersion(long Version, bool Deleted);

/// <summary>A collection of one database.</summary>
/// <param name="Name">Collection name.</param>
/// <param name="CreatedAt">Creation time in Unix milliseconds.</param>
/// <param name="Id">Canonical uppercase ULID of this collection creation.</param>
public sealed record CollectionInfo(string Name, long CreatedAt, string Id = "");

/// <summary>A document's state at a version: a JSON value, or a deletion.</summary>
/// <param name="Collection">Collection name.</param>
/// <param name="Id">Document id.</param>
/// <param name="Version">The version written; a deletion carries the version that removed the document.</param>
/// <param name="Json">UTF-8 JSON text, or <see langword="null"/> for a deletion.</param>
public sealed record DocumentChange(string Collection, string Id, long Version, ReadOnlyMemory<byte>? Json);

/// <summary>The reserved collections a client database keeps its metadata in, as documents.</summary>
public static class DocumentDelta
{
    /// <summary>
    /// Reserved collection of a client database's collections: one document per collection, a tombstone once dropped, so
    /// creations and drops replicate and join like documents. No client collection can have the name.
    /// </summary>
    internal const string Collections = "_collections";

    /// <summary>Reserved collection of a client database's secondary indexes: one definition per index, a tombstone once dropped.</summary>
    internal const string Indexes = "_indexes";

    internal const string Unique = "_unique";
}

/// <summary>Document and collection operations inside one engine transaction.</summary>
public interface IStorageTransaction : IAsyncDisposable
{
    /// <summary>Monotonic completed join work, including documents processed by derived collection clears; never a durability signal.</summary>
    long Work { get; }

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
    /// Most document changes one system database transaction may commit: a management operation (an organization's
    /// deletion, a commit of 1024 operations) changes more documents than a client transaction may, each a small delta.
    /// </summary>
    const int MaxSystemGroupChanges = 16_384;

    /// <summary>Most bytes one system database transaction's change group may hold; it travels in one 4 MiB batch.</summary>
    const int MaxSystemGroupBytes = 3 * 1024 * 1024;

    /// <summary>
    /// Identity that scopes document ETags: derived from a client database's organization, project and database ids, so
    /// it is the same on every member and distinct after deletion/recreation or copying. The system database's is fixed: it
    /// is one database on every member.
    /// </summary>
    Guid Scope { get; }

    /// <summary>Reads this file's persisted incarnation in the transaction, under the writer for write transactions.</summary>
    ValueTask<Ulid> GetIncarnationAsync(CancellationToken cancellationToken);

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
    /// Total writes and operation framing fit at most 7 MiB.
    /// Invalid keys throw <see cref="InvalidDataException"/>; exceeded write budgets throw
    /// <see cref="StorageWriteLimitException"/>. A rejected write prevents the transaction from committing.
    /// </summary>
    ValueTask<long> PutAsync(string collection, string id, ReadOnlyMemory<byte> json, CancellationToken cancellationToken);

    /// <summary>
    /// <see cref="PutAsync"/> for a client document whose <paramref name="json"/> the caller already admitted with
    /// <c>DocumentJson.Read(json, requireObject: true)</c>. Answers the version and the body a read of the document
    /// returns (canonical JSON, fields in name order), so the document is parsed once per write.
    /// </summary>
    ValueTask<(long Version, ReadOnlyMemory<byte> Stored)> PutAdmittedAsync(
        string collection, string id, ReadOnlyMemory<byte> json, CancellationToken cancellationToken);

    /// <summary>
    /// Writes a PATCH of an existing document that leaves it as <paramref name="json"/>. A client database writes
    /// <paramref name="operations"/>, in order, to the document's state, so a concurrent write of another field survives the
    /// merge and concurrent increments and set operations converge (docs/terrabase/multi-region-replication.md section 4);
    /// <paramref name="json"/> is the materialization they leave locally. The system database stores <paramref name="json"/>.
    /// Returns the version, under the limits and errors of <see cref="PutAsync"/>. Several writes of one document in one
    /// transaction share their version.
    /// </summary>
    ValueTask<long> PatchAsync(
        string collection, string id, ReadOnlyMemory<byte> json, IReadOnlyList<FieldOperation> operations, CancellationToken cancellationToken);

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
    /// Joins a replicated delta into a client database document (docs/terrabase/multi-region-replication.md section 9): the
    /// operation stamped <paramref name="at"/>, made by its node. Writes the document only when the join changed its state,
    /// with that stamp and origin, and returns whether it did, so a duplicate or replayed delta changes nothing. A delta of a
    /// collection this database does not have creates the collection, unless the collection's deletion dominates it. Unique
    /// constraints hide losers instead of failing. <c>_collections</c> documents run their collection's local DDL.
    /// </summary>
    ValueTask<bool> JoinAsync(string collection, string id, DocumentState delta, EntryVersion at, CancellationToken cancellationToken);

    /// <summary>
    /// Whether <see cref="JoinAsync"/> would change the document, read without writing. False stays false: the stored state
    /// only grows by joins, and a collection's drop and the collected floor only grow what they clear.
    /// </summary>
    ValueTask<bool> WouldJoinAsync(string collection, string id, DocumentState delta, CancellationToken cancellationToken);

    /// <summary>Joins a certified source baseline, including retained survivors below the local collected-state floor.</summary>
    ValueTask<bool> JoinBaselineAsync(string collection, string id, DocumentState state, EntryVersion at, CancellationToken cancellationToken);

    /// <summary>Records in <c>_tb_peers</c> that every change of <paramref name="origin"/> up to <paramref name="applied"/> it streamed was joined here.</summary>
    ValueTask RecordAppliedAsync(ReplicaId origin, Hlc applied, CancellationToken cancellationToken);

    /// <summary>Refuses missing origin coverage under the writer before applying a new batch or frontier.</summary>
    ValueTask RequireAppliedAsync(ReplicaId origin, Hlc expected, CancellationToken cancellationToken);

    /// <summary>
    /// <c>_tb_peers.applied</c> per origin: every change of that origin at or below its stamp is joined here (origins mint in
    /// commit order). This machine's own origin is absent: its own commits are local. Read under the writer for write
    /// transactions, so a cut taken with a write in the same transaction is ordered with it (TS-873 history erasure).
    /// </summary>
    ValueTask<IReadOnlyDictionary<ReplicaId, Hlc>> GetAppliedAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Whether a deleted document's state or a change feed event at or below <paramref name="cut"/> is still stored: ids that
    /// tombstone collection and feed retention have not yet removed (TS-873 identifier residue).
    /// </summary>
    ValueTask<bool> HoldsHistoryAtOrBelowAsync(Hlc cut, CancellationToken cancellationToken);

    /// <summary>
    /// A client database's changes feed after <paramref name="cursor"/> (docs/terrabase/multi-region-replication.md section
    /// 14): per origin, the events with a stamp above the cursor's. Without a cursor, every origin from the start of
    /// retention. With one, an origin the cursor lacks starts at its first event, and the page is
    /// <see cref="FeedPage.Expired"/> when retention deleted an event the cursor still promised: above a named origin's
    /// stamp, or of an unnamed origin and not yet expired when the cursor was issued.
    /// At most about <paramref name="maxEvents"/> events and <paramref name="maxBytes"/> of documents: a page never ends
    /// inside one (origin, stamp). Only events of <paramref name="collections"/> when given; the others still advance
    /// <see cref="FeedPage.Scanned"/>.
    /// </summary>
    ValueTask<FeedPage> ReadFeedAsync(
        FeedCursor? cursor, IReadOnlySet<string>? collections, int maxEvents, long maxBytes, CancellationToken cancellationToken);

    /// <summary>The durable feed identities without materializing current document bodies, under the same cursor and transaction bounds.</summary>
    ValueTask<FeedPage> ReadFeedMetadataAsync(
        FeedCursor? cursor, IReadOnlySet<string>? collections, int maxEvents, long maxBytes, CancellationToken cancellationToken);

    /// <summary>The cursor at the feed's head: per origin, the highest stamp in the feed.</summary>
    ValueTask<IReadOnlyDictionary<ReplicaId, Hlc>> GetFeedHeadAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Deletes feed events with a stamp below <paramref name="before"/>, at most <paramref name="limit"/>, and raises each
    /// origin's floor to the highest stamp it deleted; then forgets the origins with no event left whose floor is more than
    /// <see cref="FeedCursor.Retention"/> older than <paramref name="before"/> (<see cref="FeedCursor.Forget"/> old, for a
    /// pass that deletes what is older than the retention).
    /// Uncaptured: a restore keeps more history, never less. Returns how many events it deleted.
    /// </summary>
    ValueTask<int> TrimFeedAsync(Hlc before, int limit, CancellationToken cancellationToken);

    /// <summary>
    /// Lists live documents with ids after <paramref name="afterId"/> in id order: at most
    /// <paramref name="limit"/> documents and, beyond the first, at most <paramref name="maxBytes"/>
    /// of JSON in total. A page cut short by either bound reports <see cref="DocumentPage.HasMore"/>.
    /// </summary>
    ValueTask<DocumentPage> ListAsync(string collection, string? afterId, int limit, long maxBytes, CancellationToken cancellationToken);

    /// <summary>Runs a query over the live documents of one collection.</summary>
    /// <remarks><paramref name="observation"/> accumulates candidate visits, including sort admission, even when execution fails.</remarks>
    ValueTask<QueryPage> QueryAsync(DocumentQuery query, CancellationToken cancellationToken, QueryObservation? observation = null);

    /// <summary>The latest committed offset this transaction sees, without committing it.</summary>
    ValueTask<long> GetOffsetAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Counts live documents matching <paramref name="filter"/> while reading at most <paramref name="maxExamined"/> + 1
    /// rows (documents and tombstones) in id order. <c>Complete</c> is <see langword="false"/> when the collection holds
    /// more rows, so the count is a lower bound.
    /// </summary>
    /// <remarks><paramref name="observation"/> includes repeated visits in the separate overflow walk.</remarks>
    ValueTask<(long Count, bool Complete)> CountAsync(string collection, DocumentFilter? filter, long maxExamined, CancellationToken cancellationToken, QueryObservation? observation = null);

    /// <summary>Whether a collection (or name prefix) has documents, tombstones, collection metadata or retained replication deletions.</summary>
    ValueTask<bool> HasCollectionStateAsync(string name, bool prefix, CancellationToken cancellationToken);

    /// <summary>Creates a collection, a commit of its own offset; <see langword="false"/> when it already exists.</summary>
    ValueTask<bool> CreateCollectionAsync(CollectionInfo collection, CancellationToken cancellationToken);

    /// <summary>Reads a collection, or <see langword="null"/> when it does not exist.</summary>
    ValueTask<CollectionInfo?> GetCollectionAsync(string name, CancellationToken cancellationToken);

    /// <summary>Reads a collection's attached schema, or <see langword="null"/> when none is attached or the collection is absent.</summary>
    ValueTask<JsonElement?> GetCollectionSchemaAsync(string name, CancellationToken cancellationToken);

    /// <summary>Reads the replicated collection access policy; missing rules deny end users.</summary>
    ValueTask<CollectionAccessRules?> GetCollectionRulesAsync(string name, CancellationToken cancellationToken);

    /// <summary>Replaces the replicated declarative policy.</summary>
    ValueTask<bool> SetCollectionRulesAsync(string name, CollectionAccessRules rules, CancellationToken cancellationToken);

    /// <summary>Latest retained deletion preimage proof; absent when ownership cannot be proven.</summary>
    ValueTask<DocumentOwnerProof?> GetDocumentOwnerProofAsync(string collection, string id, CancellationToken cancellationToken);

    /// <summary>
    /// Attaches a schema of at most 64 KiB of UTF-8 JSON, or detaches it with <see langword="null"/>;
    /// <see langword="false"/> when the collection is absent. Preserves collection creation time.
    /// </summary>
    ValueTask<bool> SetCollectionSchemaAsync(string name, JsonElement? schema, CancellationToken cancellationToken);

    /// <summary>
    /// Reads a client collection's relational table definition (member <c>sql</c> of its <c>_collections</c> metadata),
    /// or <see langword="null"/> when it has none or the collection is absent.
    /// </summary>
    ValueTask<JsonElement?> GetCollectionTableAsync(string name, CancellationToken cancellationToken);

    /// <summary>
    /// Attaches or replaces a client collection's table definition, keeping every other metadata member; it replicates
    /// with the metadata (last writer wins). <see langword="false"/> when the collection is absent.
    /// </summary>
    ValueTask<bool> SetCollectionTableAsync(string name, JsonElement definition, CancellationToken cancellationToken);

    /// <summary>Reads a collection's TTL member (a JSON Pointer), or <see langword="null"/> when none is declared or the collection is absent.</summary>
    ValueTask<string?> GetCollectionTtlAsync(string name, CancellationToken cancellationToken);

    /// <summary>
    /// Declares the JSON Pointer of a collection's expiry member, or clears it with <see langword="null"/>;
    /// <see langword="false"/> when the collection is absent. Preserves the rest of the collection metadata.
    /// </summary>
    ValueTask<bool> SetCollectionTtlAsync(string name, string? path, CancellationToken cancellationToken);

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

    /// <summary>
    /// Runs one relational read (docs/terrabase/sql/relational-sql.md section 4) under the query work budget of
    /// <see cref="QueryAsync"/>: past it a <see cref="QueryCapacityException"/>. Reads at most the program's rows and bytes,
    /// fully, before returning. A statement the engine refuses throws <see cref="SqlProgramException"/>.
    /// </summary>
    ValueTask<SqlProgramResult> ExecuteSqlAsync(SqlProgram program, CancellationToken cancellationToken);

    /// <summary>Describes how the engine would run <paramref name="query"/>, without running it.</summary>
    ValueTask<SqlQueryPlan> ExplainSqlAsync(SqlQuery query, CancellationToken cancellationToken);

    /// <summary>
    /// Purges every system database collection whose name starts with <paramref name="prefix"/> from this machine: its
    /// documents and their states, without tombstones, so nothing of it replicates. For state that no live catalog document
    /// reaches any more (a deleted database's or project's), which every machine purges when it learns of the deletion.
    /// Returns how many live documents were removed. A client database refuses it with <see cref="NotSupportedException"/>.
    /// </summary>
    ValueTask<long> DeleteCollectionsAsync(string prefix, CancellationToken cancellationToken);

    /// <summary>Lists the secondary indexes of this database in name order.</summary>
    ValueTask<IReadOnlyList<IndexDefinition>> ListIndexesAsync(CancellationToken cancellationToken);

    /// <summary>Builds a secondary index over live documents; <see langword="false"/> when an index with the name already exists.</summary>
    ValueTask<bool> CreateIndexAsync(IndexDefinition index, CancellationToken cancellationToken);

    /// <summary>
    /// Adds the next bounded batch of existing documents to a vector index still building (<see cref="IndexDefinition.Building"/>);
    /// <see langword="true"/> while one remains.
    /// </summary>
    ValueTask<bool> BuildVectorIndexAsync(CancellationToken cancellationToken);

    /// <summary>Drops a secondary index; <see langword="false"/> when it did not exist.</summary>
    ValueTask<bool> DropIndexAsync(string name, CancellationToken cancellationToken);

    /// <summary>Reads the unique keys declared for a collection.</summary>
    ValueTask<IReadOnlyList<UniqueConstraint>> GetUniqueConstraintsAsync(string collection, CancellationToken cancellationToken);

    /// <summary>Reads at most <paramref name="limit"/> conflicting keys, including hidden contenders, without mutating state.</summary>
    ValueTask<IReadOnlyList<(string Constraint, string Id)>> GetUniqueConflictsAsync(
        string collection, ReadOnlyMemory<byte> json, int limit, CancellationToken cancellationToken);

    /// <summary>
    /// Declares unique keys for an empty collection; an unchanged declaration is idempotent. With <paramref name="reindex"/>, a
    /// collection with live documents is reindexed instead, and refused with <see cref="UniqueConstraintException"/> only when
    /// its documents hold a duplicate key (or a uniqueness loser is kept aside).
    /// </summary>
    ValueTask SetUniqueConstraintsAsync(string collection, IReadOnlyList<UniqueConstraint> constraints, bool reindex, CancellationToken cancellationToken);

    /// <summary>
    /// Makes every change of this transaction visible atomically and returns its offset and own client replication marker.
    /// Nonmutating transactions have no marker; their offset is the latest committed one.
    /// </summary>
    ValueTask<CommitResult> CommitAsync(CancellationToken cancellationToken);
}

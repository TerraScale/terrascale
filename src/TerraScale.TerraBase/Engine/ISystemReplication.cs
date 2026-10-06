namespace TerraScale.TerraBase.Engine;

/// <summary>A position in a journal: its 16-byte origin, a group sequence (0 before the first) and that prefix's digest.</summary>
/// <param name="Origin">The origin the journal belongs to.</param>
/// <param name="Sequence">The last group of the prefix.</param>
/// <param name="Digest">The <see cref="JournalDigest"/> of the prefix through <paramref name="Sequence"/>.</param>
public sealed record JournalPosition(ReadOnlyMemory<byte> Origin, long Sequence, ReadOnlyMemory<byte> Digest);

/// <summary>One page of the system database's live documents, as puts, in (collection, id) order.</summary>
/// <param name="Documents">The documents; at least one unless the end was reached.</param>
/// <param name="HasMore">Whether documents after the last one exist.</param>
public sealed record SystemDocumentPage(IReadOnlyList<DocumentChange> Documents, bool HasMore);

/// <summary>A run of one system journal group's changes, in order.</summary>
/// <param name="Count">How many changes the whole group holds; 0 when the journal does not hold the group.</param>
/// <param name="Changes">The changes from the requested index on.</param>
public sealed record SystemGroupPage(int Count, IReadOnlyList<DocumentChange> Changes);

/// <summary>
/// Replication of TerraBase's own database between a catalog primary and its followers (docs/decisions/0009). Every write
/// transaction of the system database is captured into its journal; a follower's engine refuses every write
/// transaction there except the ones begun here.
/// </summary>
public interface ISystemReplication
{
    /// <summary>Most changes one system database transaction captures.</summary>
    const int MaxGroupChanges = 262_144;

    /// <summary>Most collection, id and JSON bytes one system database transaction captures.</summary>
    const long MaxGroupBytes = 64L * 1024 * 1024;

    /// <summary>Groups the system journal keeps; a follower whose head is older gets a snapshot.</summary>
    /// <remarks>
    /// ponytail: a count, not bytes; about 3 hours of 10-second usage flushes. Keep a byte total per group in
    /// <c>journal_prefixes</c> (a schema change) if usage flushes of many databases make the tail large.
    /// </remarks>
    const int RetainedGroups = 1024;

    /// <summary>
    /// Reads at most <paramref name="maxDocuments"/> live documents after (<paramref name="afterCollection"/>,
    /// <paramref name="afterId"/>), stopping after the one that reaches <paramref name="maxBytes"/> of collection, id and
    /// JSON bytes. Each call is its own short read transaction.
    /// </summary>
    ValueTask<SystemDocumentPage> ReadDocumentsAsync(
        string? afterCollection, string? afterId, int maxDocuments, int maxBytes, CancellationToken cancellationToken);

    /// <summary>
    /// Reads the changes of captured group <paramref name="sequence"/> from index <paramref name="skip"/>: at most
    /// <paramref name="maxChanges"/>, stopping after the one that reaches <paramref name="maxBytes"/>. Groups never change
    /// once committed, so pages read in separate transactions fit together.
    /// </summary>
    ValueTask<SystemGroupPage> ReadGroupAsync(long sequence, int skip, int maxChanges, int maxBytes, CancellationToken cancellationToken);

    /// <summary>
    /// The head of this system database's journal: its origin (also its incarnation), last group and that prefix's digest.
    /// A follower's head is the primary group it applied last, since it adopts the primary's origin.
    /// </summary>
    ValueTask<JournalPosition> GetHeadAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Begins the follower's replication write transaction. With <paramref name="adopt"/>, it first removes every document
    /// and the follower's own journal and takes that primary origin as its own, for a snapshot. Readers see nothing of it
    /// until it commits.
    /// </summary>
    ValueTask<ISystemReplicaTransaction> BeginReplicaAsync(ReadOnlyMemory<byte>? adopt, CancellationToken cancellationToken);
}

/// <summary>A follower's replication write transaction on the system database; dispose without committing to roll back.</summary>
public interface ISystemReplicaTransaction : IAsyncDisposable
{
    /// <summary>Sets a document to exactly the state of <paramref name="change"/>: its version and JSON, or no document.</summary>
    ValueTask ReplaceAsync(DocumentChange change, CancellationToken cancellationToken);

    /// <summary>
    /// Records <paramref name="head"/>, a position of the adopted origin, as the journal head, raises the commit offset to at
    /// least <paramref name="offset"/> and commits.
    /// </summary>
    ValueTask CommitAsync(JournalPosition head, long offset, CancellationToken cancellationToken);
}

/// <summary>
/// This node follows a catalog primary, which owns every write to TerraBase's own database. The listeners answer
/// it with 503 <c>not_primary</c> and <c>Retry-After</c>.
/// </summary>
public sealed class CatalogFollowerException()
    : Exception("this node replicates the catalog from its catalog primary; send catalog writes to the primary");

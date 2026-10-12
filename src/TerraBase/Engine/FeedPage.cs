using TerraBase.Engine.Lattices;

namespace TerraBase.Engine;

/// <summary>
/// One event of a client database's changes feed (docs/terrabase/multi-region-replication.md section 14): an operation of
/// <paramref name="Origin"/> at <paramref name="Stamp"/> that changed a document on this machine.
/// </summary>
/// <param name="Origin">The node that made the operation.</param>
/// <param name="Stamp">The operation's stamp.</param>
/// <param name="Collection">The document's collection.</param>
/// <param name="Id">The document's id.</param>
/// <param name="Deleted">Whether the operation left the document deleted.</param>
/// <param name="Document">The document's current JSON on this machine; null when it does not exist here now.</param>
public sealed record FeedEvent(ReplicaId Origin, Hlc Stamp, string Collection, string Id, bool Deleted, ReadOnlyMemory<byte>? Document);

/// <summary>A page of the changes feed after a cursor.</summary>
/// <param name="Events">In (stamp, origin) order; each origin's events in its commit order.</param>
/// <param name="Scanned">
/// The cursor after the page: per origin, the highest stamp read, events the collection filter left out included.
/// </param>
/// <param name="More">Whether events past <paramref name="Scanned"/> may already be waiting.</param>
/// <param name="Expired">The cursor is below an origin's retention floor: events it has not seen are gone (410).</param>
public sealed record FeedPage(IReadOnlyList<FeedEvent> Events, IReadOnlyDictionary<ReplicaId, Hlc> Scanned, bool More, bool Expired);

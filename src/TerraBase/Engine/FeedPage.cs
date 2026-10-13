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
public sealed record FeedEvent(ReplicaId Origin, Hlc Stamp, string Collection, string Id, bool Deleted, ReadOnlyMemory<byte>? Document)
{
    /// <summary>The origin prefix through this event.</summary>
    public FeedPrefix? Prefix { get; init; }
}

/// <summary>A page of the changes feed after a cursor.</summary>
/// <param name="Events">In (stamp, origin) order; each origin's events in its commit order.</param>
/// <param name="Scanned">
/// The cursor after the page: per origin, the highest stamp read, events the collection filter left out included.
/// </param>
/// <param name="More">Whether events past <paramref name="Scanned"/> may already be waiting.</param>
/// <param name="Expired">The retention floor or origin prefix cannot satisfy the cursor (410).</param>
public sealed record FeedPage(IReadOnlyList<FeedEvent> Events, IReadOnlyDictionary<ReplicaId, Hlc> Scanned, bool More, bool Expired)
{
    /// <summary>The machine whose local prefix hashes this page carries.</summary>
    public ReplicaId Issuer { get; init; }

    /// <summary>The origin prefixes through every scanned event.</summary>
    public IReadOnlyDictionary<ReplicaId, FeedPrefix> Prefixes { get; init; } = new Dictionary<ReplicaId, FeedPrefix>();

    /// <summary>The validated prefixes before this page's events.</summary>
    public IReadOnlyDictionary<ReplicaId, FeedPrefix> Before { get; init; } = new Dictionary<ReplicaId, FeedPrefix>();
}

/// <summary>
/// Where a changes feed reader resumes (docs/terrabase/multi-region-replication.md section 14): per origin, the highest
/// stamp delivered, and when the cursor was issued. An origin the cursor lacks had no event on the issuing machine, so an
/// event of it that retention deleted and that was not yet expired when the cursor was issued is one the reader missed.
/// </summary>
/// <param name="Stamps">Per origin, the highest stamp delivered.</param>
/// <param name="Issued">When the cursor was issued, as a stamp of that instant.</param>
public sealed record FeedCursor(IReadOnlyDictionary<ReplicaId, Hlc> Stamps, Hlc Issued)
{
    /// <summary>The issuing Fly machine, so the proxy can resume the stream on its original history.</summary>
    public string? Machine { get; init; }

    /// <summary>The machine whose local prefix hashes this cursor carries.</summary>
    public ReplicaId Issuer { get; init; }

    /// <summary>Proof of the events scanned for each named origin.</summary>
    public IReadOnlyDictionary<ReplicaId, FeedPrefix> Prefixes { get; init; } = new Dictionary<ReplicaId, FeedPrefix>();

    /// <summary>How long the feed keeps an event.</summary>
    public static readonly TimeSpan Retention = TimeSpan.FromDays(7);

    /// <summary>
    /// The floor of an origin with no event left is forgotten once it is this old, so an origin whose events all aged out
    /// leaves the feed (and the cursors). A cursor issued longer ago than <see cref="Retention"/> could have missed such an
    /// origin's events, so it is expired.
    /// </summary>
    public static readonly TimeSpan Forget = 2 * Retention;

    /// <summary>The stamp of <paramref name="instant"/>: its milliseconds, logical part 0.</summary>
    public static Hlc At(DateTimeOffset instant) => new((ulong)instant.ToUnixTimeMilliseconds() << 16);
}

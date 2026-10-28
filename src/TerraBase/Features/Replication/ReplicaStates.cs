using TerraBase.Engine.Lattices;
using TerraBase.Tenancy;

namespace TerraBase.Features.Replication;

/// <summary>A document's whole state, as anti-entropy reads and joins it.</summary>
public sealed record RepairState(string Collection, string Id, DocumentState State);

/// <summary>Where a bucket read resumes: after this document of this bucket.</summary>
public sealed record BucketCursor(int Bucket, string Collection, string Id);

/// <summary>One incarnation of this machine's copy of a database and its bucket digests.</summary>
/// <param name="Incarnation">The copy the digests were read from.</param>
/// <param name="Head">Its newest change id, or 0, read in the same snapshot as the digests.</param>
/// <param name="Buckets">4096 × (h1, h2), little-endian: the XOR of each bucket's document digests.</param>
/// <param name="Feed">Its feed head (<c>seq</c>), or 0, read in the same snapshot.</param>
public sealed record ReplicaDigest(Ulid Incarnation, long Head, byte[] Buckets, long Feed)
{
    /// <summary>The durable collected-state frontier, read in the same snapshot as the digests.</summary>
    public Hlc CollectedBefore { get; init; }
}

/// <summary>
/// What anti-entropy reads of this machine's copy of a database (docs/terrabase/multi-region-replication.md section 10):
/// its bucket digests and the states of chosen buckets.
/// </summary>
public interface IReplicaStates
{
    /// <summary>
    /// The digests of the copy in place; null when this machine does not hold the database, or when
    /// <paramref name="expected"/> names another incarnation. Only a discovery (an anti-entropy round) names none.
    /// Each bucket XORs digests of (collection, id, state without bookkeeping, materialized document).
    /// </summary>
    ValueTask<ReplicaDigest?> DigestAsync(TenantContext database, Ulid? expected, CancellationToken cancellationToken);

    /// <summary>
    /// As <see cref="DigestAsync"/>, from the digests the engine maintains instead of a scan of every state row: a restored
    /// or behind copy differs from the peers' all the same, and only a row changed outside the engine needs the scan (the
    /// next anti-entropy round finds it). What a catalog gate's pull at start compares.
    /// </summary>
    ValueTask<ReplicaDigest?> MaintainedDigestAsync(TenantContext database, Ulid? expected, CancellationToken cancellationToken) =>
        DigestAsync(database, expected, cancellationToken);

    /// <summary>
    /// A machine of another placed region answered digests equal to <paramref name="digest"/>: it holds every value of that
    /// snapshot, so the shape frontier (FR-25) rises to its feed head when the copy in place is still that incarnation.
    /// True when the frontier moved.
    /// </summary>
    ValueTask<bool> MatchedAsync(TenantContext database, ReplicaDigest digest, CancellationToken cancellationToken);

    /// <summary>
    /// The whole states of <paramref name="buckets"/> of <paramref name="incarnation"/> after <paramref name="after"/>: at most
    /// <paramref name="maxStates"/>, and <paramref name="maxBytes"/> of encoded states unless the first alone is larger; and
    /// where the next page starts, or null. Null when this machine does not hold the database or the copy in place is another.
    /// An empty bucket list checks only that the named incarnation is still held.
    /// </summary>
    ValueTask<(IReadOnlyList<RepairState> States, BucketCursor? Next)?> ReadBucketsAsync(
        TenantContext database, Ulid incarnation, IReadOnlyList<int> buckets, BucketCursor? after, int maxStates, int maxBytes, CancellationToken cancellationToken);

    /// <summary>This machine's stable stamp of the database (section 11), as its acks report it.</summary>
    ValueTask<Hlc> StableAsync(TenantContext database, CancellationToken cancellationToken);

    /// <summary>This machine's GC horizon of the database: below it, every replica holds every event.</summary>
    ValueTask<Hlc> HorizonAsync(TenantContext database, CancellationToken cancellationToken);

    /// <summary>
    /// A copying refill: forgets each of <paramref name="keys"/> of <paramref name="incarnation"/> whose state lies wholly
    /// below <paramref name="horizon"/>, the horizon of a peer that no longer holds it. Returns how many went; null, with
    /// nothing forgotten, when this machine does not hold the database, the copy in place is another, or a live local
    /// state lacks deletion evidence (checked under the writer).
    /// </summary>
    ValueTask<int?> ForgetAsync(
        TenantContext database, Ulid incarnation, IReadOnlyList<(string Collection, string Id)> keys, Hlc horizon, CancellationToken cancellationToken);
}

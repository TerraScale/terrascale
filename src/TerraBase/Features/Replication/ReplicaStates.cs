using TerraBase.Engine.Lattices;
using TerraBase.Tenancy;

namespace TerraBase.Features.Replication;

/// <summary>A document's whole state, as anti-entropy reads and joins it.</summary>
public sealed record RepairState(string Collection, string Id, DocumentState State);

/// <summary>
/// What a refilling peer forgets and adopts (docs/terrabase/multi-region-replication.md section 10): the highest horizon
/// below which the sender dropped states, and its applied stamp per origin (its own: its clock as the refill began). A state
/// the sender lacks, wholly below <paramref name="CollectedBelow"/>, with an entry the sender applied, was collected there.
/// </summary>
public sealed record RefillBasis(Hlc CollectedBelow, IReadOnlyDictionary<ReplicaId, Hlc> Applied);

/// <summary>Where a bucket read resumes: after this document of this bucket.</summary>
public sealed record BucketCursor(int Bucket, string Collection, string Id);

/// <summary>
/// What anti-entropy reads of this machine's copy of a database (docs/terrabase/multi-region-replication.md section 10):
/// its bucket digests and the states of chosen buckets.
/// </summary>
public interface IReplicaStates
{
    /// <summary>4096 × (h1, h2), little-endian: the XOR of each bucket's document digests; null when this machine does not hold the database.</summary>
    ValueTask<byte[]?> DigestAsync(TenantContext database, CancellationToken cancellationToken);

    /// <summary>
    /// The whole states of <paramref name="buckets"/> after <paramref name="after"/>: at most <paramref name="maxStates"/>, and
    /// <paramref name="maxBytes"/> of encoded states unless the first alone is larger; and where the next page starts, or null.
    /// </summary>
    ValueTask<(IReadOnlyList<RepairState> States, BucketCursor? Next)> ReadBucketsAsync(
        TenantContext database, IReadOnlyList<int> buckets, BucketCursor? after, int maxStates, int maxBytes, CancellationToken cancellationToken);

    /// <summary>This machine's stable stamp of the database (section 11), as its acks report it.</summary>
    ValueTask<Hlc> StableAsync(TenantContext database, CancellationToken cancellationToken);

    /// <summary>
    /// What a peer refilled from this machine needs (section 10): its collection watermark and its applied stamps; null when
    /// this machine does not hold the database.
    /// </summary>
    ValueTask<RefillBasis?> RefillBasisAsync(TenantContext database, CancellationToken cancellationToken);

    /// <summary>
    /// A copying refill: forgets each of <paramref name="keys"/>, states the refilling peer lacks, that it collected (see
    /// <see cref="RefillBasis"/>). Returns how many went.
    /// </summary>
    ValueTask<int> ForgetAsync(TenantContext database, IReadOnlyList<(string Collection, string Id)> keys, RefillBasis basis, CancellationToken cancellationToken);

    /// <summary>After a refill from the peer of <paramref name="basis"/>: this machine holds every event it applied, and its collections.</summary>
    ValueTask AdoptAsync(TenantContext database, RefillBasis basis, CancellationToken cancellationToken);

    /// <summary>The highest stamp this machine applied from any epoch of <paramref name="origin"/>'s machine; null when it does not hold the database.</summary>
    ValueTask<Hlc?> AppliedFromAsync(TenantContext database, ReplicaId origin, CancellationToken cancellationToken);

    /// <summary><paramref name="peer"/>'s highest stable stamp recorded here (0 when none): what it must have applied from this machine.</summary>
    ValueTask<Hlc> ReportedStableAsync(TenantContext database, ReplicaId peer, CancellationToken cancellationToken);
}

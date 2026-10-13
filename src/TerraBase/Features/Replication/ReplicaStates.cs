using TerraBase.Engine.Lattices;
using TerraBase.Tenancy;

namespace TerraBase.Features.Replication;

/// <summary>A document's whole state, as anti-entropy reads and joins it.</summary>
public sealed record RepairState(string Collection, string Id, DocumentState State);

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
}

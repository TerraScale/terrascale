using TerraBase.Engine.Lattices;
using TerraBase.Tenancy;

namespace TerraBase.Features.Replication;

/// <summary>One bounded page of a single read snapshot. Only the last page carries its applied vector.</summary>
public sealed record ReplicaSnapshotPage(IReadOnlyList<RepairState> States, IReadOnlyDictionary<ReplicaId, Hlc>? Applied = null, Hlc CollectedBefore = default);

/// <summary>Exports whole state and received coverage from the same pinned read transaction.</summary>
public interface IReplicaSnapshotSource
{
    /// <summary>Throws or ends without a terminal vector when the named copy cannot be certified.</summary>
    IAsyncEnumerable<ReplicaSnapshotPage> SnapshotAsync(TenantContext database, Ulid incarnation, CancellationToken cancellationToken);
}

/// <summary>Atomically imports a pinned baseline and received-prefix proof, preserving survivor and deletion evidence.</summary>
internal interface IReplicaSnapshotImporter
{
    /// <summary>
    /// False without taking the writer when the staged replay's estimated work exceeds <paramref name="holdBudget"/>: a
    /// caller that may wait (background recovery) must not hold a serving database's writer for minutes.
    /// </summary>
    Task<bool> ImportAsync(TenantContext database, Ulid incarnation, IAsyncEnumerable<ReplicaSnapshotPage> pages,
        IReadOnlySet<ulong> origins, CancellationToken cancellationToken, TimeSpan? holdBudget = null);
}

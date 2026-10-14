using TerraBase.Features.Replication;
using TerraBase.Tenancy;

namespace TerraBase.Engine;

/// <summary>
/// Durable copy faults of this node (docs/terrabase/decisions/0013a, section 1.3): a database incarnation whose file may
/// hold an uncertain or damaged commit acknowledges, certifies and accepts ordinary writes no more, across restarts, until
/// a rebuild certifies a new incarnation.
/// </summary>
internal interface IReplicaCopyFaults
{
    /// <summary>
    /// Whether <paramref name="incarnation"/> of the database must not serve as a source, acknowledge or certify: it is
    /// faulted, a rebuild of the database is not certified yet, it is not the file in place, or the node failed closed.
    /// </summary>
    ValueTask<bool> RefusedAsync(TenantContext database, Ulid incarnation, CancellationToken cancellationToken);

    /// <summary>
    /// Faults <paramref name="incarnation"/>: refused in memory at once, then recorded with FULL in the system database.
    /// When the record cannot be written the node fails closed. Signals <see cref="IRebuildRecovery.WaitAsync"/>.
    /// </summary>
    ValueTask RecordAsync(TenantContext database, Ulid incarnation, string reason, CancellationToken cancellationToken);
}

/// <summary>The engine half of certifying a rebuilt copy (section 1): the only writes a copy under rebuild accepts.</summary>
internal interface IRebuildRepair
{
    /// <summary>
    /// Joins <paramref name="states"/> as repairs (origin 0) into exactly <paramref name="newIncarnation"/>, in FULL
    /// transactions whose writer checks the file's incarnation, while its rebuild record is swapped or salvaged. The number
    /// of states that changed, or null when that incarnation is not the one under rebuild in place.
    /// </summary>
    ValueTask<int?> RepairRebuildAsync(TenantContext database, Ulid newIncarnation, IReadOnlyList<RepairState> states, CancellationToken cancellationToken);

    /// <summary>
    /// Records, with FULL, the certified refill of <paramref name="newIncarnation"/>: the listed member it came from (its
    /// membership name), that source's incarnation and its head change id. False unless the record is swapped with that
    /// incarnation in place.
    /// </summary>
    ValueTask<bool> RecordRefillAsync(
        TenantContext database, Ulid newIncarnation, string sourceMachine, Ulid sourceIncarnation, long head, CancellationToken cancellationToken);

    /// <summary>
    /// The completion barrier: a FULL write of the refill's certificate row into the rebuilt file (its incarnation checked
    /// under its writer), then the record's move from salvaged to certified in one FULL system transaction. False, changing
    /// nothing more, unless the record is salvaged for exactly these incarnations with a refill of this node recorded.
    /// Safe to repeat after a crash between the two.
    /// </summary>
    ValueTask<bool> CompleteRebuildAsync(TenantContext database, Ulid oldIncarnation, Ulid newIncarnation, CancellationToken cancellationToken);
}

/// <summary>
/// A write refused because the database's copy on this node is faulted or under rebuild, or because the node failed
/// closed. Retrying on this node does not help until a rebuild certifies a new incarnation.
/// </summary>
internal sealed class ReplicaCopyFaultedException(string message) : InvalidOperationException(message);

namespace TerraBase.E2E;

/// <summary>
/// Region offline and rejoin on ts-orch, for each region in turn: removes the region from the disposable service, writes,
/// deletes and conflicts through both survivors, applies the service with every region again and waits until the region
/// serves both backlogs, the tombstones and the settled conflict (<see cref="ReplicationMatrixTests.RejoinAsync"/>, the same
/// proof the local matrix runs). It stops regions, so it runs alone (<c>make terrabase-e2e-orch-outage</c>), never in the
/// deploy run, and only against <see cref="Orch.Service"/> in a pinned run with <c>TERRABASE_E2E_ORCH_OUTAGE=1</c>
/// (docs/terrabase/e2e-ts-orch.md).
/// </summary>
[Category("orch-outage")]
[Category("replication-matrix-orch-outage")]
public sealed class OrchOutageE2ETests
{
    // One test for all three outages, in sequence: no two outages share the service's regions at the same time.
    [Test]
    [Scenario(Nodes.InProcess, Faults.NodeStop)]
    public async Task Each_region_stopped_then_started_catches_up_on_writes_tombstones_and_conflicts_from_both_survivors(CancellationToken cancellationToken)
    {
        Skip.Unless(Cluster.LiveOutage, Cluster.OutageRun);
        foreach (var offline in new[] { Cluster.Gru, Cluster.Sjc, Cluster.Fra })
        {
            cancellationToken.ThrowIfCancellationRequested();
            // A collection per outage: its digest names only this outage's documents, and its cleanup runs with every region up.
            await ReplicationMatrixTests.RejoinOwnedAsync(offline, cancellationToken);
        }
    }
}

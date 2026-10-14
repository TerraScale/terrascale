namespace TerraBase.E2E;

/// <summary>
/// Region offline and rejoin on Fly, for each region in turn: stops its machine, writes, deletes and conflicts through
/// both survivors, starts it again and waits until it serves both backlogs, the tombstones and the settled conflict
/// (<see cref="ReplicationMatrixTests.RejoinAsync"/>, the same proof the local matrix runs). It stops machines, so it runs
/// alone (<c>make terrabase-e2e-fly-outage</c>), never in the deploy run, and only on a pinned disposable app
/// (<c>TERRABASE_E2E_FLY_REVISION</c> and <c>TERRABASE_E2E_FLY_APP</c>, never production; see
/// docs/terrabase/e2e-disposable-app.md) with <c>TERRABASE_E2E_FLY_OUTAGE=1</c>.
/// </summary>
[Category("fly-outage")]
[Category("replication-matrix-fly-outage")]
public sealed class FlyOutageE2ETests
{
    // Three outages in sequence, each with machine stop and start waits and the convergence deadlines.
    [Test]
    [Timeout(30 * 60_000)]
    [Scenario(Nodes.InProcess, Faults.NodeStop)]
    public async Task Each_region_machine_stopped_then_started_catches_up_on_writes_tombstones_and_conflicts_from_both_survivors(CancellationToken cancellationToken)
    {
        Skip.Unless(Cluster.FlyRequested && Cluster.FlyMachineFaults,
            "Fly outage test: a pinned disposable app (TERRABASE_E2E_FLY_URL, TERRABASE_E2E_FLY_APP, TERRABASE_E2E_FLY_REVISION) and TERRABASE_E2E_FLY_OUTAGE=1");
        foreach (var offline in new[] { Cluster.Gru, Cluster.Sjc, Cluster.Fra })
        {
            cancellationToken.ThrowIfCancellationRequested();
            // A collection per outage: its digest names only this outage's documents, and its cleanup runs with every machine up.
            await using var cluster = await Cluster.StartAsync(Cluster.Gru, Cluster.Sjc, Cluster.Fra);
            await ReplicationMatrixTests.RejoinAsync(cluster, offline, cancellationToken);
        }
    }
}

using System.Net;

namespace TerraBase.E2E;

/// <summary>
/// Cancellation during a real local outage must recover the region before returning the original error. It stops a local
/// node owned by this test.
/// </summary>
[Category("local-only")]
public sealed class RejoinRecoveryTests
{
    [Test]
    [Scenario(Nodes.InProcess, Faults.NodeStop)]
    public async Task Canceling_a_rejoin_while_the_region_is_down_restarts_it_before_returning()
    {
        await using var cluster = await Cluster.StartLocalAsync(Cluster.Gru, Cluster.Sjc, Cluster.Fra);
        using var cancellation = new CancellationTokenSource();
        var rejoin = ReplicationMatrixTests.RejoinAsync(cluster, Cluster.Sjc, cancellation.Token);
        _ = await Converge.UntilAsync(async () =>
        {
            if (rejoin.IsCompleted)
            {
                await rejoin;
            }

            try
            {
                _ = cluster.Endpoint(Cluster.Sjc);
                return false;
            }
            catch (InvalidOperationException error) when (error.Message == "sjc is down")
            {
                await cancellation.CancelAsync();
                return true;
            }
        }, down => down, "sjc stops before cancellation");

        var canceled = await Assert.ThrowsAsync<OperationCanceledException>(() => rejoin);
        await Assert.That(canceled!.CancellationToken).IsEqualTo(cancellation.Token);
        await Assert.That(await Docs.StatusAsync(cluster[Cluster.Sjc], new Uri("/healthz", UriKind.Relative))).IsEqualTo(HttpStatusCode.OK);
    }
}

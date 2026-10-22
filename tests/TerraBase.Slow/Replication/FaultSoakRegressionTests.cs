using System.Net;

namespace TerraBase.Slow.Replication;

public sealed class FaultSoakRegressionTests
{
    [Test]
    [Timeout(180_000)]
    public async Task Empty_change_feed_opens_on_a_clock_skewed_three_node_cluster(CancellationToken token)
    {
        await using var cluster = await FaultSoakCluster.StartAsync(0, token);
        var client = cluster["gru"];
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{cluster.DatabasePath}/changes?collections={cluster.Collection}")
        {
            Version = client.DefaultRequestVersion,
            VersionPolicy = client.DefaultVersionPolicy,
        };
        request.Headers.Accept.ParseAdd("text/event-stream");
        using var answer = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        await Assert.That(answer.StatusCode).IsEqualTo(HttpStatusCode.OK)
            .Because($"seed=0; no writes or network faults; {cluster.Diagnostics}");
    }
}

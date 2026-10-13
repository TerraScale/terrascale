using System.Diagnostics;
using System.Text.Json.Nodes;
using TerraBase.Primitives;

namespace TerraBase.E2E;

public sealed class F4RestartDiagnosticTests
{
    [Test]
    public async Task All_three_nodes_restart_together_and_accept_a_write()
    {
        if (Cluster.FlyRequested)
        {
            throw new InvalidOperationException("F4 diagnostic runs only in-process");
        }

        await using var cluster = await Cluster.StartAsync(Cluster.Gru, Cluster.Sjc, Cluster.Fra);
        var uri = cluster.Document(EntityIds.New().ToString());
        await Converge.SeedAsync(cluster, uri, new JsonObject { ["n"] = 1 });
        await Task.WhenAll(cluster.Regions.Select(cluster.StopAsync));
        var started = Stopwatch.GetTimestamp();
        await Task.WhenAll(cluster.Regions.Select(cluster.StartAgainAsync));
        Console.WriteLine($"F4 simultaneous restart returned in {Stopwatch.GetElapsedTime(started).TotalSeconds:F1} s");
        foreach (var region in cluster.Regions)
        {
            await Docs.PutAsync(cluster[region], cluster.Document(EntityIds.New().ToString()), new JsonObject { ["n"] = 2 });
        }
    }
}

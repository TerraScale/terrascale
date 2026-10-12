using System.Text.Json.Nodes;
using TerraBase.Primitives;

namespace TerraBase.E2E;

/// <summary>The region-process harness itself: separate OS processes find each other, replicate, and survive a SIGKILL.</summary>
public sealed class RegionProcessesTests
{
    [Test]
    [Scenario(Nodes.Processes, Faults.ProcessKill)]
    public async Task Region_processes_replicate_and_a_killed_region_catches_up_after_its_restart()
    {
        Skip.When(Cluster.FlyRequested, "runs local node processes");
        await using var regions = await RegionProcesses.StartAsync(Cluster.Gru, Cluster.Sjc, Cluster.Fra);
        var first = regions.Document(EntityIds.New().ToString());
        await Docs.PutAsync(regions[Cluster.Gru], first, new JsonObject { ["step"] = 1 });
        foreach (var region in regions.Regions)
        {
            _ = await Converge.UntilAsync(() => Docs.ReadAsync(regions[region], first), document => document?["step"]?.GetValue<int>() == 1, $"gru's write on {region}");
        }

        // sjc is killed; gru writes while it is gone; once sjc is back it holds the write.
        await regions.KillAsync(Cluster.Sjc);
        var second = regions.Document(EntityIds.New().ToString());
        await Docs.PutAsync(regions[Cluster.Gru], second, new JsonObject { ["step"] = 2 });
        await regions.StartAgainAsync(Cluster.Sjc);
        _ = await Converge.UntilAsync(() => Docs.ReadAsync(regions[Cluster.Sjc], second), document => document?["step"]?.GetValue<int>() == 2, "gru's write on sjc after its restart");
    }
    // Acknowledged before it could ship: with sjc and fra down, gru acknowledges writes (they exist only in gru's file and
    // its turso_cdc), then is killed with SIGKILL. Once every region is back, each holds exactly the acknowledged writes:
    // the change groups survived the kill on disk and shipped from gru's restarted process.
    [Test]
    [Scenario(Nodes.Processes, Faults.ProcessKill)]
    public async Task Writes_a_region_acknowledged_before_it_could_ship_them_reach_every_region_after_a_SIGKILL()
    {
        Skip.When(Cluster.FlyRequested, "runs local node processes");
        await using var regions = await RegionProcesses.StartAsync(Cluster.Gru, Cluster.Sjc, Cluster.Fra);
        await regions.KillAsync(Cluster.Sjc);
        await regions.KillAsync(Cluster.Fra);
        var acknowledged = new List<(Uri Uri, int Step)>();
        for (var step = 0; step < 200; step++)
        {
            var uri = regions.Document(EntityIds.New().ToString());
            await Docs.PutAsync(regions[Cluster.Gru], uri, new JsonObject { ["step"] = step });
            acknowledged.Add((uri, step));
        }

        await regions.KillAsync(Cluster.Gru);
        await regions.StartAgainAsync(Cluster.Sjc);
        await regions.StartAgainAsync(Cluster.Fra);
        await regions.StartAgainAsync(Cluster.Gru);
        foreach (var region in regions.Regions)
        {
            foreach (var (uri, step) in acknowledged)
            {
                _ = await Converge.UntilAsync(() => Docs.ReadAsync(regions[region], uri), document => document?["step"]?.GetValue<int>() == step,
                    $"gru's acknowledged write {step} on {region} after gru's SIGKILL");
            }
        }
    }
}

using System.Text.Json.Nodes;
using TerraBase.Primitives;

namespace TerraBase.E2E;

/// <summary>The region-process harness itself: separate OS processes find each other, replicate, and survive a SIGKILL.</summary>
public sealed class RegionProcessesTests
{
    [Test]
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
}

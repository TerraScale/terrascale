using System.Net;
using System.Text.Json;
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

    [Test]
    [Scenario(Nodes.Processes, Faults.ProcessKill)]
    public async Task Writes_are_refused_while_all_placed_peers_are_down_and_resume_after_a_peer_rejoins()
    {
        await using var regions = await RegionProcesses.StartAsync(Cluster.Gru, Cluster.Sjc, Cluster.Fra);
        await regions.KillAsync(Cluster.Sjc);
        await regions.KillAsync(Cluster.Fra);
        _ = await Converge.UntilAsync(StatusAsync, status => status.GetProperty("peers").GetArrayLength() == 0,
            "gru has no connected peers");
        var uri = regions.Document(EntityIds.New().ToString());
        using var refused = await regions[Cluster.Gru].PutAsJsonAsync(uri, new JsonObject { ["step"] = 1 });
        await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        var error = await refused.Content.ReadFromJsonAsync<JsonElement>();
        await Assert.That(error.GetProperty("error").GetProperty("code").GetString()).IsEqualTo("remote_region_unavailable");
        await Assert.That(refused.Headers.RetryAfter).IsNotNull();
        using var absent = await regions[Cluster.Gru].GetAsync(uri);
        await Assert.That(absent.StatusCode).IsEqualTo(HttpStatusCode.NotFound);

        await regions.StartAgainAsync(Cluster.Sjc);
        // Without external catalog anchors, a restarted test node must verify every recorded witness before serving tenants.
        await regions.StartAgainAsync(Cluster.Fra);
        _ = await Converge.UntilAsync(() => regions[Cluster.Sjc].GetFromJsonAsync<JsonElement>(new Uri("/v1/catalog-gate", UriKind.Relative)),
            gate => gate.GetProperty("open").GetBoolean(), "sjc's catalog verifies after its restart");
        await regions.KillAsync(Cluster.Fra);
        _ = await Converge.UntilAsync(StatusAsync, status => status.GetProperty("peers").EnumerateArray()
            .Select(peer => peer.GetProperty("region").GetString()).SequenceEqual([Cluster.Sjc]), "only sjc is connected after catalog recovery");
        using var remoteAbsent = await regions[Cluster.Sjc].GetAsync(uri);
        await Assert.That(remoteAbsent.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        using var accepted = await regions[Cluster.Gru].PutAsJsonAsync(uri, new JsonObject { ["step"] = 2 });
        await Assert.That(accepted.StatusCode).IsEqualTo(HttpStatusCode.Created).Because(await accepted.Content.ReadAsStringAsync());
        await Assert.That((await accepted.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("durability").GetString()).IsEqualTo("remote_durable");
        using var remote = await regions[Cluster.Sjc].GetAsync(uri);
        await Assert.That(remote.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That((await remote.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("document").GetProperty("step").GetInt32()).IsEqualTo(2);

        async Task<JsonElement> StatusAsync()
        {
            return await regions[Cluster.Gru].GetFromJsonAsync<JsonElement>(new Uri("/replication", UriKind.Relative));
        }
    }
}

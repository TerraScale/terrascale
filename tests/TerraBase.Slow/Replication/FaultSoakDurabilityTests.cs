using System.Net;
using System.Text.Json;

namespace TerraBase.Slow.Replication;

[Category("replication-fault-soak")]
public sealed class FaultSoakDurabilityTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    [Timeout(300_000)]
    public async Task Acknowledgement_survives_immediate_SIGKILL_and_loss_of_unsynced_WAL(bool remote, CancellationToken token)
    {
        await using var cluster = remote ? await FaultSoakCluster.StartAsync(0, token) : await FaultSoakCluster.StartLocalAsync(0, token);
        if (remote)
        {
            cluster.SlowDisk("sjc", 50);
            cluster.SlowDisk("fra", 100);
        }

        var id = FaultSoakChecks.Id(0, 500);
        using var answer = await cluster["gru"].PutAsJsonAsync(cluster.Document(id), new { value = "ack-before-kill" }, token);
        // No remote read or convergence wait may rescue an acknowledgement issued before remote fsync.
        await Task.WhenAll(cluster.Regions.Select(cluster.KillAsync));
        await Assert.That(answer.StatusCode).IsEqualTo(HttpStatusCode.Created);
        var receipt = await answer.Content.ReadFromJsonAsync<JsonElement>(token);
        await Assert.That(receipt.GetProperty("durability").GetString()).IsEqualTo(remote ? "remote_durable" : "local_durable");
        FaultSoakChecks.Check(!remote || cluster.DelayedSyncs("sjc") + cluster.DelayedSyncs("fra") > 0,
            "the remote durability scenario did not exercise a delayed WAL sync");
        foreach (var region in cluster.Regions)
        {
            cluster.DropUnsynced(region);
        }

        // Inspect copies while every process is dead: repair cannot mask lost acknowledged bytes.
        var local = await cluster.ReadStoppedDocumentAsync("gru", id, token);
        FaultSoakChecks.Check(local?["value"]?.GetValue<string>() == "ack-before-kill", "local acknowledged bytes were lost");
        if (remote)
        {
            var remotes = await Task.WhenAll(cluster.Regions.Where(region => region != "gru")
                .Select(region => cluster.ReadStoppedDocumentAsync(region, id, token)));
            FaultSoakChecks.Check(remotes.Any(copy => copy?["value"]?.GetValue<string>() == "ack-before-kill"),
                "no remotely acknowledged copy retained its bytes");
        }
    }
}

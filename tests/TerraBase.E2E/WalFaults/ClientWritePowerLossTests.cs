using System.Text.Json.Nodes;
using TerraBase.Primitives;

namespace TerraBase.E2E.WalFaults;

/// <summary>
/// Barrier B1: a client database's write commits with a synced write-ahead log on the machine that accepted it, before it
/// is answered. So an answer waits for its commit's sync (a document write and a direct transaction alike), and every
/// write the machine answered is still there after a power loss drops every unsynced byte of every log.
/// </summary>
public sealed class ClientWritePowerLossTests
{
    [Test]
    [Scenario(Nodes.Processes, Faults.ProcessKill | Faults.FileTampering)]
    public async Task A_power_loss_keeps_every_client_write_the_node_answered()
    {
        Skip.When(WalSyncFaults.Library is null, "needs Linux and cc to build the write-ahead log fault shim");
        // Standalone WAL host: recovery must prove the local disk, without a cluster catalog waiting for a peer.
        await using var node = await PowerLossNode.StartAsync(nameof(TenantPowerLossHost));
        var (organization, project) = (EntityIds.New().ToString(), EntityIds.New().ToString());
        await node.CommitAsync(EntityIds.New().ToString(), new JsonObject
        {
            ["id"] = EntityIds.New().ToString(),
            ["ops"] = new JsonArray(
                new JsonObject { ["op"] = "create_organization", ["id"] = organization, ["name"] = $"power-{organization}" },
                new JsonObject { ["op"] = "create_project", ["organization"] = organization, ["id"] = project, ["name"] = "power" },
                new JsonObject { ["op"] = "create_database", ["organization"] = organization, ["project"] = project, ["name"] = "canary", ["id"] = EntityIds.New().ToString(), ["replica_regions"] = new JsonArray(Cluster.Sjc) }),
        });
        var path = $"/v1/projects/{project}/databases/canary";
        await Cluster.CreateCollectionAsync(new(StringComparer.Ordinal) { [Cluster.Gru] = node.Client }, path, "items");

        string[] ids = [EntityIds.New().ToString(), EntityIds.New().ToString(), EntityIds.New().ToString(), EntityIds.New().ToString(), EntityIds.New().ToString()];
        foreach (var (index, id) in ids.Take(3).Index())
        {
            await Docs.PutAsync(node.Client, Document(path, id), new JsonObject { ["n"] = index });
        }

        // With every log sync held, a document write and a two-document transaction are each answered only after a sync.
        var delay = TimeSpan.FromMilliseconds(500);
        node.DelaySyncs(delay);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        await Docs.PutAsync(node.Client, Document(path, ids[3]), new JsonObject { ["n"] = 3 });
        await Assert.That(clock.Elapsed).IsGreaterThanOrEqualTo(delay).Because("a document write is answered after its commit's sync");
        clock.Restart();
        using (var transaction = await node.Client.PostAsJsonAsync(new Uri($"{path}/transactions", UriKind.Relative), new JsonObject
        {
            ["ops"] = new JsonArray(
                new JsonObject { ["op"] = "put", ["collection"] = "items", ["id"] = ids[4], ["document"] = new JsonObject { ["n"] = 4 } },
                new JsonObject { ["op"] = "put", ["collection"] = "items", ["id"] = ids[0], ["document"] = new JsonObject { ["n"] = 0 } }),
        }))
        {
            await Docs.EnsureSuccessAsync(transaction);
        }

        await Assert.That(clock.Elapsed).IsGreaterThanOrEqualTo(delay).Because("a transaction is answered after its commit's sync");
        node.ClearFaults();

        await node.PowerLossAsync();
        await node.StartAgainAsync();

        foreach (var (index, id) in ids.Index())
        {
            var document = await Docs.ReadAsync(node.Client, Document(path, id));
            await Assert.That(document?["n"]?.GetValue<int>()).IsEqualTo(index).Because($"write {index + 1} was answered before the power loss");
        }
    }

    private static Uri Document(string path, string id) => new($"{path}/collections/items/documents/by-key?id={id}", UriKind.Relative);
}

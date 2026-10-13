using System.Text.Json.Nodes;
using TerraBase.Primitives;

namespace TerraBase.E2E.WalFaults;

/// <summary>
/// Barrier B1 of decision 0013: a client database's write commits with a synced write-ahead log on the machine that
/// accepted it, so every write it answered is still there after a power loss of that machine drops every unsynced byte.
/// </summary>
public sealed class ClientWritePowerLossTests
{
    [Test]
    [Scenario(Nodes.Processes, Faults.ProcessKill | Faults.FileTampering)]
    public async Task A_power_loss_keeps_every_client_write_the_node_answered()
    {
        Skip.When(Cluster.FlyRequested, "kills a local node process and edits its files");
        Skip.When(WalSyncFaults.Library is null, "needs Linux and cc to build the write-ahead log fault shim");
        await using var node = await PowerLossNode.StartAsync();
        var (organization, project) = (EntityIds.New().ToString(), EntityIds.New().ToString());
        await node.CommitAsync(EntityIds.New().ToString(), new JsonObject
        {
            ["id"] = EntityIds.New().ToString(),
            ["ops"] = new JsonArray(
                new JsonObject { ["op"] = "create_organization", ["id"] = organization, ["name"] = $"power-{organization}" },
                new JsonObject { ["op"] = "create_project", ["organization"] = organization, ["id"] = project, ["name"] = "power" },
                new JsonObject { ["op"] = "create_database", ["organization"] = organization, ["project"] = project, ["name"] = "canary", ["id"] = EntityIds.New().ToString() }),
        });
        var path = $"/v1/projects/{project}/databases/canary";
        await Cluster.CreateCollectionAsync(new(StringComparer.Ordinal) { [Cluster.Gru] = node.Client }, path, "items");

        string[] ids = [EntityIds.New().ToString(), EntityIds.New().ToString(), EntityIds.New().ToString()];
        foreach (var (index, id) in ids.Index())
        {
            await Docs.PutAsync(node.Client, Document(path, id), new JsonObject { ["n"] = index });
        }

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

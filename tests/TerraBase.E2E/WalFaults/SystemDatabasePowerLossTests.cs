using System.Net;
using System.Text.Json.Nodes;
using TerraBase.Primitives;

namespace TerraBase.E2E.WalFaults;

public sealed class SystemDatabasePowerLossTests
{
    [Test]
    [Scenario(Nodes.Processes, Faults.ProcessKill | Faults.FileTampering)]
    public async Task A_power_loss_keeps_the_catalog_and_the_api_keys_the_system_database_acknowledged()
    {
        Skip.When(WalSyncFaults.Library is null, "needs Linux and cc to build the write-ahead log fault shim");
        await using var node = await PowerLossNode.StartAsync(nameof(TenantPowerLossHost));
        var (organization, project) = (EntityIds.New().ToString(), EntityIds.New().ToString());
        var owner = EntityIds.New().ToString();
        await node.CommitAsync(owner, new JsonObject
        {
            ["id"] = EntityIds.New().ToString(),
            ["ops"] = new JsonArray(
                new JsonObject { ["op"] = "create_organization", ["id"] = organization, ["name"] = $"power-{organization}" },
                new JsonObject { ["op"] = "create_project", ["organization"] = organization, ["id"] = project, ["name"] = "power" },
                new JsonObject { ["op"] = "create_database", ["organization"] = organization, ["project"] = project, ["name"] = "canary", ["id"] = EntityIds.New().ToString(), ["replica_regions"] = new JsonArray() }),
        });
        var path = $"/v1/projects/{project}/databases/canary";
        await Cluster.CreateCollectionAsync(new(StringComparer.Ordinal) { [Cluster.Gru] = node.Client }, path, "items");
        var read = new Uri($"{path}/collections/items/documents/by-key?id={EntityIds.New()}", UriKind.Relative);
        await Docs.PutAsync(node.Client, read, new JsonObject { ["n"] = 1 });
        using var created = await node.Client.PostAsJsonAsync("/admin/api-keys", new JsonObject
        {
            ["name"] = "power loss",
            ["scopes"] = new JsonArray("documents:read"),
            ["organizationId"] = organization,
            ["projectId"] = project,
        });
        await Docs.EnsureSuccessAsync(created);
        var secret = (await created.Content.ReadFromJsonAsync<JsonObject>())!["secret"]!.GetValue<string>();
        await Assert.That(await node.StatusAsync(read, null)).IsEqualTo(HttpStatusCode.OK).Because("the operator reaches the registered project");
        await Assert.That(await node.StatusAsync(read, secret)).IsEqualTo(HttpStatusCode.OK).Because("the new key authenticates");

        await node.PowerLossAsync();
        await node.StartAgainAsync();

        await Assert.That(await node.StatusAsync(read, null)).IsEqualTo(HttpStatusCode.OK)
            .Because("the project's registration was acknowledged before the power loss");
        await Assert.That(await node.StatusAsync(read, secret)).IsEqualTo(HttpStatusCode.OK)
            .Because("the key was acknowledged before the power loss");
    }
}

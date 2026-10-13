using System.Net;
using System.Text.Json.Nodes;
using TerraBase.Primitives;

namespace TerraBase.E2E.WalFaults;

/// <summary>
/// The system database's acknowledged writes survive a power loss of their node: its commits sync the write-ahead log
/// (<c>synchronous = FULL</c>), unlike a tenant database's (decision 0003). Its organization, project, database
/// registration and API key are all still there after every unsynced byte of every log is dropped.
/// </summary>
public sealed class SystemDatabasePowerLossTests
{
    [Test]
    [Scenario(Nodes.Processes, Faults.ProcessKill | Faults.FileTampering)]
    public async Task A_power_loss_keeps_the_catalog_and_the_api_keys_the_system_database_acknowledged()
    {
        Skip.When(Cluster.FlyRequested, "kills a local node process and edits its files");
        Skip.When(WalSyncFaults.Library is null, "needs Linux and cc to build the write-ahead log fault shim");
        await using var node = await PowerLossNode.StartAsync();
        var (organization, project) = (EntityIds.New().ToString(), EntityIds.New().ToString());
        var owner = EntityIds.New().ToString();
        await node.CommitAsync(owner, new JsonObject
        {
            ["id"] = EntityIds.New().ToString(),
            ["ops"] = new JsonArray(
                new JsonObject { ["op"] = "create_organization", ["id"] = organization, ["name"] = $"power-{organization}" },
                new JsonObject { ["op"] = "create_project", ["organization"] = organization, ["id"] = project, ["name"] = "power" },
                new JsonObject { ["op"] = "create_database", ["organization"] = organization, ["project"] = project, ["name"] = "canary", ["id"] = EntityIds.New().ToString() }),
        });
        using var created = await node.Client.PostAsJsonAsync("/admin/api-keys", new JsonObject
        {
            ["name"] = "power loss",
            ["scopes"] = new JsonArray("documents:read"),
            ["organizationId"] = organization,
            ["projectId"] = project,
        });
        await Docs.EnsureSuccessAsync(created);
        var secret = (await created.Content.ReadFromJsonAsync<JsonObject>())!["secret"]!.GetValue<string>();
        var read = new Uri($"/v1/projects/{project}/databases/canary/collections/items/documents/by-key?id={EntityIds.New()}", UriKind.Relative);
        await Assert.That(await node.StatusAsync(read, null)).IsNotEqualTo(HttpStatusCode.Unauthorized).Because("the operator reaches the registered project");
        await Assert.That(await node.StatusAsync(read, secret)).IsNotEqualTo(HttpStatusCode.Unauthorized).Because("the new key authenticates");

        await node.PowerLossAsync();
        await node.StartAgainAsync();

        await Assert.That(await node.StatusAsync(read, null)).IsNotEqualTo(HttpStatusCode.Unauthorized)
            .Because("the project's registration was acknowledged before the power loss");
        await Assert.That(await node.StatusAsync(read, secret)).IsNotEqualTo(HttpStatusCode.Unauthorized)
            .Because("the key was acknowledged before the power loss");
    }
}

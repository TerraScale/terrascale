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
        Skip.When(WalSyncFaults.Library is null, "needs Linux and cc to build the write-ahead log fault shim");
        await using var node = await PowerLossNode.StartAsync(nameof(TenantPowerLossHost));
        var owner = EntityIds.New().ToString();
        var createdDatabase = await node.CreateDatabaseAsync(owner, [Cluster.Sjc]);
        var organization = createdDatabase.Organization;
        var project = createdDatabase.Project;
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

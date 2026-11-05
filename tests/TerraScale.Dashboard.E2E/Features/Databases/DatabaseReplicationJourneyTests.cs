using System.Net;
using System.Text.Json;

namespace TerraScale.Dashboard.E2E.Features.Databases;

[ClassDataSource<DashboardRootFixture>]
public sealed class DatabaseReplicationJourneyTests(DashboardRootFixture fixture)
{
    [Test]
    public async Task OwnerAddsAndRemovesReplicaRegionsThroughTheReplicationTab()
    {
        await using var scenario = await DatabaseSdkScenario.StartAsync(fixture.Root);
        using var client = scenario.CreateClient();
        using var controls = scenario.CreateProviderClient();
        using (var catalog = await controls.PostAsJsonAsync("/__test/control", new { command = "catalog", value = new[] { "gru", "sfo", "sjc", "fra" } }))
        {
            await Assert.That(catalog.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        }

        await DatabaseJourney.NativeWorkspaceAsync(client);
        var id = await HttpJourney.CreateDatabaseAsync(client, "replicas-journey");
        var csrf = await HttpJourney.CsrfAsync(client, "/databases/" + id + "/replication");

        using (var saved = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/databases/replicas", new()
        { ["_csrf"] = csrf, ["databaseId"] = id, ["replicaRegions"] = "sjc" }))
        {
            await Assert.That(saved.StatusCode).IsEqualTo(HttpStatusCode.OK).Because(await saved.Content.ReadAsStringAsync());
            using var placed = JsonDocument.Parse(await saved.Content.ReadAsStringAsync());
            var regions = placed.RootElement.GetProperty("regions").EnumerateArray().Select(region => region.GetString() ?? "").ToArray();
            await Assert.That(regions).IsEquivalentTo(new[] { "GRU", "SJC" });
        }

        var html = WebUtility.HtmlDecode(await client.GetStringAsync("/databases/" + id + "/replication?saved=1"));
        await Assert.That(html).Contains("data-replica-region=\"sjc\"");
        await Assert.That(html).Contains("Replica regions saved.");
        await Assert.That(html).Contains("name=\"replicaRegions\" value=\"sjc\" checked");
        using (var observations = await controls.GetAsync("/__test/observations"))
        {
            var producer = JsonDocument.Parse(await observations.Content.ReadAsStringAsync()).RootElement.GetProperty("databases");
            var placed = producer.EnumerateArray().Single(database => database.GetProperty("id").GetString() == id);
            await Assert.That(placed.GetProperty("homeRegion").GetString()).IsEqualTo("gru");
            await Assert.That(placed.GetProperty("replicaRegions")[0].GetString()).IsEqualTo("sjc");
        }

        using (var removed = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/databases/replicas", new()
        { ["_csrf"] = csrf, ["databaseId"] = id }))
        {
            await Assert.That(removed.StatusCode).IsEqualTo(HttpStatusCode.OK).Because(await removed.Content.ReadAsStringAsync());
            using var placed = JsonDocument.Parse(await removed.Content.ReadAsStringAsync());
            var regions = placed.RootElement.GetProperty("regions").EnumerateArray().Select(region => region.GetString() ?? "").ToArray();
            await Assert.That(regions).IsEquivalentTo(new[] { "GRU" });
        }

        html = WebUtility.HtmlDecode(await client.GetStringAsync("/databases/" + id + "/replication"));
        await Assert.That(html).DoesNotContain("data-replica-region=");
        await Assert.That(html).Contains("No replica regions.");
    }

    [Test]
    public async Task ViewerSeesNoControlsAndIsRefused()
    {
        await using var scenario = await DatabaseSdkScenario.StartAsync(fixture.Root);
        using var client = scenario.CreateClient();
        await DatabaseJourney.NativeWorkspaceAsync(client);
        var id = await HttpJourney.CreateDatabaseAsync(client, "replicas-viewer");
        var csrf = await HttpJourney.CsrfAsync(client, "/databases/" + id + "/replication");
        using var controls = scenario.CreateProviderClient();
        using (var role = await controls.PostAsJsonAsync("/__test/control", new { command = "role", value = "viewer" }))
        {
            await Assert.That(role.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        }

        var html = WebUtility.HtmlDecode(await client.GetStringAsync("/databases/" + id + "/replication"));
        await Assert.That(html).DoesNotContain("name=\"replicaRegions\"");
        await Assert.That(html).DoesNotContain("Save replica regions");
        using (var refused = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/databases/replicas", new()
        { ["_csrf"] = csrf, ["databaseId"] = id, ["replicaRegions"] = "sjc" }))
        {
            await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.Forbidden).Because(await refused.Content.ReadAsStringAsync());
        }
    }
}

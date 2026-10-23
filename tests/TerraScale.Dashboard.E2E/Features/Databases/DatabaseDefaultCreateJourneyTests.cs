using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace TerraScale.Dashboard.E2E.Features.Databases;

[ClassDataSource<DashboardRootFixture>]
public sealed class DatabaseDefaultCreateJourneyTests(DashboardRootFixture fixture)
{
    // The create form's defaults: only the home region checked, All regions unticked. TerraBase places such a database in every
    // other serving region, and deletes it only once those replicas are removed.
    [Test]
    public async Task DefaultCreateSucceedsAndTheDatabaseCanBeDeleted()
    {
        await using var scenario = await DatabaseSdkScenario.StartAsync(fixture.Root);
        using var client = scenario.CreateClient();
        using var producer = scenario.CreateProviderClient();
        await DatabaseSdkJourneyTests.NativeWorkspaceAsync(client);
        using (var region = await producer.PostAsJsonAsync("/__test/control", new { command = "api-region", value = "sfo" }))
            await Assert.That(region.IsSuccessStatusCode).IsTrue();
        var csrf = await HttpJourney.CsrfAsync(client, "/databases");

        string databaseId;
        using (var created = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/databases", new() { ["_csrf"] = csrf, ["name"] = "orders", ["region"] = "SFO" }))
        {
            await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created).Because(await created.Content.ReadAsStringAsync());
            using var body = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
            databaseId = body.RootElement.GetProperty("id").GetString()!;
            await Assert.That(body.RootElement.GetProperty("allRegions").GetBoolean()).IsFalse();
            await Assert.That(body.RootElement.GetProperty("regions").EnumerateArray().Select(value => value.GetString()!).ToArray()).IsEquivalentTo(new[] { "SFO", "GRU" });
        }

        using (var deleted = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/databases/delete", new() { ["_csrf"] = csrf, ["databaseId"] = databaseId, ["confirmName"] = "orders" }))
            await Assert.That(deleted.StatusCode).IsEqualTo(HttpStatusCode.NoContent).Because(await deleted.Content.ReadAsStringAsync());
        using (var listed = JsonDocument.Parse(await client.GetStringAsync("/api/v1/dashboard/databases")))
            await Assert.That(listed.RootElement.GetArrayLength()).IsEqualTo(0);
        using var observations = JsonDocument.Parse(await producer.GetStringAsync("/__test/observations"));
        await Assert.That(observations.RootElement.GetProperty("failures").GetArrayLength()).IsEqualTo(0);
    }
}

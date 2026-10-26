using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TerraScale.Dashboard.Features.Accounts;
using TerraScale.Dashboard.Shared.Databases;

namespace TerraScale.Dashboard.E2E.Features.Databases;

[ClassDataSource<DashboardRootFixture>]
public sealed class DatabaseDefaultCreateJourneyTests(DashboardRootFixture fixture)
{
    [Test]
    public async Task NamesKeepTheDashboardSubsetAndRejectDataPlaneReservedNames()
    {
        foreach (var name in new[] { "a", "a1", "a--b", "orders-2026", "con-db", "console", "com10", "lpt10", new string('a', 63) })
            await Assert.That(DatabaseService.ValidName(name)).IsEqualTo(name);
        var invalid = new[] { "", "1database", "0", "-orders", "orders-", "Orders", "order_db", "orders.db", "a\nb", new string('a', 64), "con", "prn", "aux", "nul" }
            .Concat(Enumerable.Range(0, 10).SelectMany(number => new[] { "com" + number, "lpt" + number }));
        foreach (var name in invalid)
        {
            AccountAccessException? rejected = null;
            try { DatabaseService.ValidName(name); }
            catch (AccountAccessException error) { rejected = error; }
            await Assert.That(rejected?.StatusCode).IsEqualTo(422).Because("Invalid database name: " + name);
            await Assert.That(rejected?.Message).Contains("must start with a lowercase letter");
            await Assert.That(rejected?.Message).Contains("Reserved names:");
        }
    }

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
        var html = WebUtility.HtmlDecode(await client.GetStringAsync("/databases"));
        await Assert.That(html).Contains("pattern=\"" + DatabaseService.NameInputPattern + "\"");
        await Assert.That(html).Contains(DatabaseService.NameHint);
        foreach (var name in new[] { "1database", "con", "com0", "lpt9" })
        {
            using var rejected = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/databases", new()
            { ["_csrf"] = csrf, ["name"] = name, ["region"] = "SFO" });
            await Assert.That(rejected.StatusCode).IsEqualTo(HttpStatusCode.UnprocessableEntity);
            await Assert.That(await rejected.Content.ReadAsStringAsync()).Contains("must start with a lowercase letter");
        }
        using (var beforeCreate = JsonDocument.Parse(await producer.GetStringAsync("/__test/observations")))
            await Assert.That(beforeCreate.RootElement.GetProperty("creates").GetInt32()).IsEqualTo(0);

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

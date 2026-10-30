using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TerraScale.Dashboard.E2E.Features.Databases
{
    [ClassDataSource<DashboardRootFixture>]
    public sealed class DatabaseDefaultCreateJourneyTests(DashboardRootFixture fixture)
    {
        [Test]
        public async Task NamesKeepTheDashboardSubsetAndRejectDataPlaneReservedNames()
        {
            foreach (var name in new[] { "a", "a1", "a--b", "orders-2026", "con-db", "console", "com10", "lpt10", new string('a', 63) })
            {
                await Assert.That(DatabaseService.ValidName(name)).IsEqualTo(name);
            }

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

        // The form explicitly selects all serving regions today; future-region tracking remains a separate choice.
        [Test]
        public async Task DefaultCreateSucceedsAndTheDatabaseCanBeDeleted()
        {
            await using var scenario = await DatabaseSdkScenario.StartAsync(fixture.Root);
            using var client = scenario.CreateClient();
            using var producer = scenario.CreateProviderClient();
            await DatabaseSdkJourneyTests.NativeWorkspaceAsync(client);
            using (var region = await producer.PostAsJsonAsync("/__test/control", new { command = "catalog", value = new[] { "gru", "fra", "sfo" } }))
            {
                await Assert.That(region.IsSuccessStatusCode).IsTrue();
            }

            var csrf = await HttpJourney.CsrfAsync(client, "/databases");
            var html = WebUtility.HtmlDecode(await client.GetStringAsync("/databases"));
            await Assert.That(html).Contains("pattern=\"" + DatabaseService.NameInputPattern + "\"");
            await Assert.That(html).Contains(DatabaseService.NameHint);
            await Assert.That(html).Contains("All currently serving regions are selected by default.");
            await Assert.That(html).Contains("Selecting every current region does not include future regions.");
            var selected = Regex.Matches(html, "<input[^>]+type=\"checkbox\"[^>]+name=\"region\"[^>]+value=\"([^\"]+)\"[^>]*checked[^>]*>")
                .Select(match => match.Groups[1].Value).ToArray();
            await Assert.That(selected).IsEquivalentTo(new[] { "GRU", "FRA", "SFO" });
            await Assert.That(Regex.Match(html, "<option value=\"GRU\"[^>]*>").Value).Contains("selected");
            var allRegionsInput = Regex.Match(html, "<input[^>]+name=\"allRegions\"[^>]*>").Value;
            await Assert.That(allRegionsInput.Length > 0).IsTrue();
            await Assert.That(Regex.IsMatch(allRegionsInput, "\\schecked(?:\\s|=|/?>)")).IsFalse();
            await Assert.That(html.Contains("One region keeps the data only there", StringComparison.Ordinal)).IsFalse();
            foreach (var name in new[] { "1database", "con", "com0", "lpt9" })
            {
                using var rejected = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/databases", new()
                { ["_csrf"] = csrf, ["name"] = name, ["region"] = "SFO" });
                await Assert.That(rejected.StatusCode).IsEqualTo(HttpStatusCode.UnprocessableEntity);
                await Assert.That(await rejected.Content.ReadAsStringAsync()).Contains("must start with a lowercase letter");
            }
            using (var beforeCreate = JsonDocument.Parse(await producer.GetStringAsync("/__test/observations")))
            {
                await Assert.That(beforeCreate.RootElement.GetProperty("creates").GetInt32()).IsEqualTo(0);
            }

            string databaseId;
            using (var created = await client.PostAsync("/api/v1/dashboard/databases", new FormUrlEncodedContent(
                new[] { new KeyValuePair<string, string>("_csrf", csrf), new("name", "orders"), new("homeRegion", "GRU") }
                    .Concat(selected.Select(region => new KeyValuePair<string, string>("region", region))))))
            {
                await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created).Because(await created.Content.ReadAsStringAsync());
                using var body = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
                databaseId = body.RootElement.GetProperty("id").GetString()!;
                await Assert.That(body.RootElement.GetProperty("allRegions").GetBoolean()).IsFalse();
                await Assert.That(body.RootElement.GetProperty("regions").EnumerateArray().Select(value => value.GetString()!).ToArray()).IsEquivalentTo(new[] { "GRU", "FRA", "SFO" });
            }

            var replication = WebUtility.HtmlDecode(await client.GetStringAsync("/databases/" + databaseId + "/replication"));
            await Assert.That(replication).Contains("is the home region and stays selected.");
            await Assert.That(replication).Contains("With All regions cleared, keep at least two regions.");
            await Assert.That(replication.Contains("adding replica regions is unavailable here", StringComparison.Ordinal)).IsFalse();

            using (var deleted = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/databases/delete", new() { ["_csrf"] = csrf, ["databaseId"] = databaseId, ["confirmName"] = "orders" }))
            {
                await Assert.That(deleted.StatusCode).IsEqualTo(HttpStatusCode.NoContent).Because(await deleted.Content.ReadAsStringAsync());
            }

            await Assert.That((await ManagementListJourney.ReadListAllAsync(client, "/api/v1/dashboard/databases")).GetArrayLength()).IsEqualTo(0);
            using var observations = JsonDocument.Parse(await producer.GetStringAsync("/__test/observations"));
            await Assert.That(observations.RootElement.GetProperty("failures").GetArrayLength()).IsEqualTo(0);
        }

        [Test]
        [Arguments("GRU,SJC", false)]
        [Arguments("GRU,FRA,SJC", false)]
        [Arguments("GRU,FRA", false)]
        [Arguments("SJC", false)]
        [Arguments("", true)]
        public async Task ChosenHomeIsUsedWithoutAddingUnselectedRegions(string placement, bool allRegions)
        {
            await using var scenario = await DatabaseSdkScenario.StartAsync(fixture.Root);
            using var client = scenario.CreateClient();
            using var producer = scenario.CreateProviderClient();
            await DatabaseSdkJourneyTests.NativeWorkspaceAsync(client);
            using var catalog = await producer.PostAsJsonAsync("/__test/control", new { command = "catalog", value = new[] { "gru", "fra", "sjc" } });
            using var answering = await producer.PostAsJsonAsync("/__test/control", new { command = "api-region", value = "sjc" });
            var csrf = await HttpJourney.CsrfAsync(client, "/databases");
            var selected = placement.Split(',', StringSplitOptions.RemoveEmptyEntries);
            using var response = await client.PostAsync("/api/v1/dashboard/databases", new FormUrlEncodedContent(
                new[] { new KeyValuePair<string, string>("_csrf", csrf), new("name", "chosen-home"), new("homeRegion", "SJC"), new("allRegions", allRegions ? "true" : "false") }
                    .Concat(selected.Select(region => new KeyValuePair<string, string>("region", region)))));
            if (!allRegions && selected.Length < 2)
            {
                await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.UnprocessableEntity);
                await Assert.That(await response.Content.ReadAsStringAsync()).Contains(DatabaseService.MinimumRegionsMessage);
                using var observations = JsonDocument.Parse(await producer.GetStringAsync("/__test/observations"));
                await Assert.That(observations.RootElement.GetProperty("creates").GetInt32()).IsEqualTo(0);
                return;
            }
            if (!allRegions && !selected.Contains("SJC"))
            {
                await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.UnprocessableEntity);
                await Assert.That(await response.Content.ReadAsStringAsync()).Contains(DatabaseService.HomePlacementMessage);
                using var observations = JsonDocument.Parse(await producer.GetStringAsync("/__test/observations"));
                await Assert.That(observations.RootElement.GetProperty("creates").GetInt32()).IsEqualTo(0);
                return;
            }
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Created).Because(await response.Content.ReadAsStringAsync());
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            await Assert.That(body.RootElement.GetProperty("region").GetString()).IsEqualTo("SJC");
            await Assert.That(body.RootElement.GetProperty("allRegions").GetBoolean()).IsEqualTo(allRegions);
            if (!allRegions)
            {
                await Assert.That(body.RootElement.GetProperty("regions").EnumerateArray().Select(value => value.GetString()!).ToArray()).IsEquivalentTo(selected);
            }
        }
    }
}

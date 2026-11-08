using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TerraScale.Dashboard.E2E.Features.Databases
{
    [ClassDataSource<DashboardRootFixture>]
    public sealed class DatabaseDefaultCreateJourneyTests(DashboardRootFixture fixture)
    {
        // A fresh workspace's empty Databases page carries the create control, and the Overview CTAs open its dialog directly.
        [Test]
        public async Task EmptyDatabasesPageCreatesThroughTheDialogAndTheOverviewLinksOpenIt()
        {
            await using var scenario = await DatabaseSdkScenario.StartAsync(fixture.Root);
            using var client = scenario.CreateClient();
            using var producer = scenario.CreateProviderClient();
            await DatabaseJourney.NativeWorkspaceAsync(client);
            using (var region = await producer.PostAsJsonAsync("/__test/control", new { command = "catalog", value = new[] { "gru", "sjc" } }))
            {
                await Assert.That(region.IsSuccessStatusCode).IsTrue();
            }

            var html = WebUtility.HtmlDecode(await client.GetStringAsync("/databases"));
            await Assert.That(html).Contains("No databases yet.");
            await Assert.That(html).Contains("command=\"show-modal\" commandfor=\"create-database\"");
            await Assert.That(html).Contains("<form method=\"post\" action=\"/api/v1/dashboard/databases\"");

            // The Overview header, Getting started step and Connect empty state deep-link the same dialog.
            var overview = WebUtility.HtmlDecode(await client.GetStringAsync("/"));
            await Assert.That(overview).Contains("href=\"/databases?create=1\"");
            await Assert.That(overview).Contains("<span>New database</span>");
            await Assert.That(overview).Contains("href=\"/databases?create=1\">Open databases");
            var connect = WebUtility.HtmlDecode(await client.GetStringAsync("/?tab=connect"));
            await Assert.That(connect).Contains("href=\"/databases?create=1\">Create a database");

            var csrf = await HttpJourney.CsrfAsync(client, "/databases");
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));

            using (var created = await client.PostAsync("/api/v1/dashboard/databases", new FormUrlEncodedContent(
                new[] { new KeyValuePair<string, string>("_csrf", csrf), new("name", "orders"), new("homeRegion", "GRU") })))
            {
                await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.SeeOther).Because(await created.Content.ReadAsStringAsync());
                await Assert.That(created.Headers.Location?.ToString()).EndsWith("?created=1");
            }

            // A plan-limit refusal re-renders the same page with the dialog open and the server message inline.
            using (var control = await producer.PostAsJsonAsync("/__test/control", new { command = "create-conflict", value = "database_limit" }))
            {
                await Assert.That(control.IsSuccessStatusCode).IsTrue();
            }

            using (var refused = await client.PostAsync("/api/v1/dashboard/databases", new FormUrlEncodedContent(
                new Dictionary<string, string> { ["_csrf"] = csrf, ["name"] = "second", ["homeRegion"] = "GRU" })))
            {
                await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
                var page = WebUtility.HtmlDecode(await refused.Content.ReadAsStringAsync());
                await Assert.That(page).Contains("id=\"create-database\"");
                await Assert.That(page).Contains("Database not created");
                await Assert.That(page).Contains("el.showModal()");
                await Assert.That(page).Contains("value=\"second\"");
            }
        }

        // A create that names only a name and a home region places no replicas.
        [Test]
        public async Task DefaultCreateSucceedsWithOnlyAHomeRegionAndTheDatabaseCanBeDeleted()
        {
            await using var scenario = await DatabaseSdkScenario.StartAsync(fixture.Root);
            using var client = scenario.CreateClient();
            using var producer = scenario.CreateProviderClient();
            await DatabaseJourney.NativeWorkspaceAsync(client);
            using (var region = await producer.PostAsJsonAsync("/__test/control", new { command = "catalog", value = new[] { "gru", "fra", "sfo" } }))
            {
                await Assert.That(region.IsSuccessStatusCode).IsTrue();
            }

            var csrf = await HttpJourney.CsrfAsync(client, "/databases");
            var html = WebUtility.HtmlDecode(await client.GetStringAsync("/databases"));
            await Assert.That(html).Contains("pattern=\"" + DatabaseService.NameInputPattern + "\"");
            await Assert.That(html).Contains(DatabaseService.NameHint);
            await Assert.That(Regex.Match(html, "<option value=\"GRU\"[^>]*>").Value).Contains("selected");
            await Assert.That(html.Contains("name=\"region\"", StringComparison.Ordinal)).IsFalse();
            foreach (var name in new[] { "1database", "con", "com0", "lpt9" })
            {
                using var rejected = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/databases", new()
                { ["_csrf"] = csrf, ["name"] = name, ["homeRegion"] = "SFO" });
                await Assert.That(rejected.StatusCode).IsEqualTo(HttpStatusCode.UnprocessableEntity);
                await Assert.That(await rejected.Content.ReadAsStringAsync()).Contains("must start with a lowercase letter");
            }
            using (var beforeCreate = JsonDocument.Parse(await producer.GetStringAsync("/__test/observations")))
            {
                await Assert.That(beforeCreate.RootElement.GetProperty("creates").GetInt32()).IsEqualTo(0);
            }

            string databaseId;
            using (var created = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/databases", new()
            { ["_csrf"] = csrf, ["name"] = "orders", ["homeRegion"] = "GRU" }))
            {
                await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created).Because(await created.Content.ReadAsStringAsync());
                using var body = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
                databaseId = body.RootElement.GetProperty("id").GetString()!;
                await Assert.That(body.RootElement.GetProperty("region").GetString()).IsEqualTo("GRU");
                await Assert.That(body.RootElement.GetProperty("allRegions").GetBoolean()).IsFalse();
                await Assert.That(body.RootElement.GetProperty("regions").EnumerateArray().Select(value => value.GetString()!).ToArray()).IsEquivalentTo(new[] { "GRU" });
            }

            using (var deleted = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/databases/delete", new() { ["_csrf"] = csrf, ["databaseId"] = databaseId, ["confirmName"] = "orders" }))
            {
                await Assert.That(deleted.StatusCode).IsEqualTo(HttpStatusCode.NoContent).Because(await deleted.Content.ReadAsStringAsync());
            }

            await Assert.That((await ManagementListJourney.ReadListAllAsync(client, "/api/v1/dashboard/databases")).GetArrayLength()).IsEqualTo(0);
            using var observations = JsonDocument.Parse(await producer.GetStringAsync("/__test/observations"));
            await Assert.That(observations.RootElement.GetProperty("creates").GetInt32()).IsEqualTo(1);
            await Assert.That(observations.RootElement.GetProperty("failures").GetArrayLength()).IsEqualTo(0);
        }

        // A non-form body reaches the form read, not a 500: the dashboard's form endpoints answer 415 and point at /v1 for JSON.
        [Test]
        public async Task CreateWithAJsonBodyAnswersUnsupportedMediaType()
        {
            await using var scenario = await DatabaseSdkScenario.StartAsync(fixture.Root);
            using var client = scenario.CreateClient();
            await DatabaseJourney.NativeWorkspaceAsync(client);

            using var refused = await client.PostAsJsonAsync("/api/v1/dashboard/databases", new { name = "orders", homeRegion = "GRU" });
            await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.UnsupportedMediaType);
            await Assert.That(refused.Content.Headers.ContentType?.MediaType).IsEqualTo("application/problem+json");
            await Assert.That(await refused.Content.ReadAsStringAsync()).Contains("use the /v1 API for JSON");
        }

        [Test]
        public async Task ReplicaRegionsAreAddedAndTheHomeMovesToAReplica()
        {
            await using var scenario = await DatabaseSdkScenario.StartAsync(fixture.Root);
            using var client = scenario.CreateClient();
            using var producer = scenario.CreateProviderClient();
            await DatabaseJourney.NativeWorkspaceAsync(client);
            using (var region = await producer.PostAsJsonAsync("/__test/control", new { command = "catalog", value = new[] { "gru", "fra", "sjc" } }))
            {
                await Assert.That(region.IsSuccessStatusCode).IsTrue();
            }

            var csrf = await HttpJourney.CsrfAsync(client, "/databases");
            string databaseId;
            using (var created = await client.PostAsync("/api/v1/dashboard/databases", new FormUrlEncodedContent(
                new[] { new KeyValuePair<string, string>("_csrf", csrf), new("name", "orders"), new("homeRegion", "GRU"), new("replicaRegions", "SJC") })))
            {
                await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created).Because(await created.Content.ReadAsStringAsync());
                using var body = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
                databaseId = body.RootElement.GetProperty("id").GetString()!;
                await Assert.That(body.RootElement.GetProperty("regions").EnumerateArray().Select(value => value.GetString()!).ToArray()).IsEquivalentTo(new[] { "GRU", "SJC" });
            }

            using (var placed = await client.PostAsync("/api/v1/dashboard/databases/placement", new FormUrlEncodedContent(
                new[] { new KeyValuePair<string, string>("_csrf", csrf), new("databaseId", databaseId), new("replicaRegions", "SJC"), new("replicaRegions", "FRA") })))
            {
                await Assert.That(placed.StatusCode).IsEqualTo(HttpStatusCode.OK).Because(await placed.Content.ReadAsStringAsync());
                using var body = JsonDocument.Parse(await placed.Content.ReadAsStringAsync());
                await Assert.That(body.RootElement.GetProperty("regions").EnumerateArray().Select(value => value.GetString()!).ToArray()).IsEquivalentTo(new[] { "GRU", "FRA", "SJC" });
            }

            using (var moved = await client.PostAsync("/api/v1/dashboard/databases/move-home", new FormUrlEncodedContent(
                new[] { new KeyValuePair<string, string>("_csrf", csrf), new("databaseId", databaseId), new("homeRegion", "SJC") })))
            {
                await Assert.That(moved.StatusCode).IsEqualTo(HttpStatusCode.OK).Because(await moved.Content.ReadAsStringAsync());
                using var body = JsonDocument.Parse(await moved.Content.ReadAsStringAsync());
                await Assert.That(body.RootElement.GetProperty("region").GetString()).IsEqualTo("SJC");
                await Assert.That(body.RootElement.GetProperty("regions").EnumerateArray().Select(value => value.GetString()!).ToArray()).IsEquivalentTo(new[] { "SJC", "FRA", "GRU" });
            }

            var page = WebUtility.HtmlDecode(await client.GetStringAsync(DatabaseService.Path(databaseId, "replication")));
            await Assert.That(page).Contains("data-home-region=\"SJC\"");
            await Assert.That(page).Contains("action=\"/api/v1/dashboard/databases/move-home\"");

            using (var external = await producer.PostAsJsonAsync("/__test/control", new { command = "external-home", name = "orders", value = "fra" }))
            {
                await Assert.That(external.IsSuccessStatusCode).IsTrue();
            }

            using var listing = JsonDocument.Parse(await client.GetStringAsync("/api/v1/dashboard/databases"));
            await Assert.That(listing.RootElement.GetProperty("items")[0].GetProperty("region").GetString()).IsEqualTo("FRA");
            var refreshed = WebUtility.HtmlDecode(await client.GetStringAsync(DatabaseService.Path(databaseId, "replication")));
            await Assert.That(refreshed).Contains("data-home-region=\"FRA\"");
        }
    }
}

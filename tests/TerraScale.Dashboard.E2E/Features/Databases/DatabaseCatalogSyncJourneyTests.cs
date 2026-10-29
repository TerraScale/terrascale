using System.Net;
using System.Text.Json;

namespace TerraScale.Dashboard.E2E.Features.Databases
{
    // TerraBase owns databases: the dashboard's list and MCP follow its listing, and a delete TerraBase already committed succeeds.
    [ClassDataSource<DashboardRootFixture>]
    public sealed class DatabaseCatalogSyncJourneyTests(DashboardRootFixture fixture)
    {
        [Test]
        public async Task DatabasesCreatedOrDeletedOutsideTheDashboardAreListedAsTerraBaseHasThem()
        {
            await using var scenario = await DatabaseSdkScenario.StartAsync(fixture.Root);
            using var client = scenario.CreateClient();
            using var producer = scenario.CreateProviderClient();
            var account = await DatabaseSdkJourneyTests.NativeWorkspaceAsync(client);
            var (organizationId, projectId) = await IdentityAsync(client);
            var secret = await HttpJourney.CreatePatAsync(client, "Catalog sync MCP", account.Password);

            // As `ts db create`: TerraBase has the database, the dashboard never saw it.
            await Control(producer, new { command = "external-create", projectId, value = "cli-made" });
            var listed = await ManagementListJourney.ReadListAllAsync(client, "/api/v1/dashboard/databases");
            var imported = listed.EnumerateArray().Single(database => database.GetProperty("name").GetString() == "cli-made");
            await Assert.That(imported.GetProperty("status").GetString()).IsEqualTo("ready");
            await Assert.That(imported.GetProperty("regions").EnumerateArray().Select(region => region.GetString()!).ToArray()).IsEquivalentTo(new[] { "GRU", "SFO" });
            var mcp = await CallAsync(client, secret, organizationId, projectId, "list_databases", new());
            await Assert.That(mcp.GetProperty("databases").EnumerateArray().Any(database => database.GetProperty("name").GetString() == "cli-made")).IsTrue();
            using (var page = await client.GetAsync("/databases"))
            {
                await Assert.That(await page.Content.ReadAsStringAsync()).Contains("cli-made");
            }

            // Deleted outside the dashboard: its record goes too, from the list page as from MCP.
            await Control(producer, new { command = "external-delete", projectId, value = "cli-made" });
            mcp = await CallAsync(client, secret, organizationId, projectId, "list_databases", new());
            await Assert.That(mcp.GetProperty("databases").EnumerateArray().Any(database => database.GetProperty("name").GetString() == "cli-made")).IsFalse();
            await Assert.That((await ManagementListJourney.ReadListAllAsync(client, "/api/v1/dashboard/databases")).EnumerateArray()
                .Any(database => database.GetProperty("name").GetString() == "cli-made")).IsFalse();
            await Assert.That((await ReadAsync(producer, "/__test/observations")).GetProperty("failures").GetArrayLength()).IsEqualTo(0);
        }

        [Test]
        public async Task DeletingAnAllRegionsDatabaseSucceedsWhenTerraBaseAnswersCommittedChangesWith503OrHasAlreadyDeletedIt()
        {
            var placementPatches = 0;
            await using var scenario = await DatabaseSdkScenario.StartAsync(fixture.Root, producerExtension: http =>
            {
                if (http.Request.Method == "PATCH" && http.Request.Path.StartsWithSegments("/v1/projects"))
                {
                    Interlocked.Increment(ref placementPatches);
                }

                return Task.FromResult<Microsoft.AspNetCore.Http.IResult?>(null);
            });
            using var client = scenario.CreateClient();
            using var producer = scenario.CreateProviderClient();
            var account = await DatabaseSdkJourneyTests.NativeWorkspaceAsync(client);
            var (organizationId, projectId) = await IdentityAsync(client);
            var secret = await HttpJourney.CreatePatAsync(client, "Catalog delete MCP", account.Password);

            var everywhere = await CallAsync(client, secret, organizationId, projectId, "create_database", new() { ["name"] = "everywhere", ["allRegions"] = true });
            var everywhereId = everywhere.GetProperty("id").GetString()!;
            // The delete answers 503 after committing; its retry finds it gone without rewriting placement.
            await Control(producer, new { command = "anchor-unavailable", patch = 0, delete = 1 });
            var deleted = await CallAsync(client, secret, organizationId, projectId, "delete_database", new() { ["databaseId"] = everywhereId, ["confirmName"] = "everywhere" });
            await Assert.That(deleted.GetProperty("deleted").GetBoolean()).IsTrue();
            await Assert.That(await RemoteAsync(producer, "everywhere")).IsFalse();
            await Assert.That(Volatile.Read(ref placementPatches)).IsEqualTo(0).Because("Deleting a replicated database sends DELETE directly, without a placement PATCH.");

            // A record whose database TerraBase already deleted is removed by the delete, which reports success.
            var stale = await CallAsync(client, secret, organizationId, projectId, "create_database", new() { ["name"] = "stale", ["allRegions"] = true });
            await Control(producer, new { command = "external-delete", projectId, value = "stale" });
            deleted = await CallAsync(client, secret, organizationId, projectId, "delete_database", new() { ["databaseId"] = stale.GetProperty("id").GetString(), ["confirmName"] = "stale" });
            await Assert.That(deleted.GetProperty("deleted").GetBoolean()).IsTrue();
            await Assert.That((await ManagementListJourney.ReadListAllAsync(client, "/api/v1/dashboard/databases")).EnumerateArray()
                .Any(database => database.GetProperty("name").GetString() is "everywhere" or "stale")).IsFalse();
            await Assert.That((await ReadAsync(producer, "/__test/observations")).GetProperty("failures").GetArrayLength()).IsEqualTo(0);
        }

        private static async Task<bool> RemoteAsync(HttpClient producer, string name)
        {
            return (await ReadAsync(producer, "/__test/observations"))
            .GetProperty("databases").EnumerateArray().Any(database => database.GetProperty("name").GetString() == name);
        }

        private static async Task<(string OrganizationId, string ProjectId)> IdentityAsync(HttpClient client)
        {
            var identity = await ReadAsync(client, "/api/v1/auth/me");
            return (identity.GetProperty("organizationId").GetString()!, identity.GetProperty("projectId").GetString()!);
        }

        private static async Task Control(HttpClient producer, object command)
        {
            using var response = await producer.PostAsJsonAsync("/__test/control", command);
            await Assert.That(response.IsSuccessStatusCode).IsTrue();
        }

        private static async Task<JsonElement> CallAsync(HttpClient client, string secret, string organizationId, string projectId, string name, Dictionary<string, object?> arguments)
        {
            arguments.TryAdd("organizationId", organizationId);
            arguments.TryAdd("projectId", projectId);
            var result = (await HttpJourney.McpAsync(client, secret, "tools/call", new { name, arguments })).GetProperty("result");
            await Assert.That(result.TryGetProperty("isError", out var error) && error.GetBoolean()).IsFalse().Because(result.GetRawText());
            return result.GetProperty("structuredContent");
        }

        private static async Task<JsonElement> ReadAsync(HttpClient client, string path)
        {
            using var response = await client.GetAsync(path);
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
            using var value = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return value.RootElement.Clone();
        }
    }
}

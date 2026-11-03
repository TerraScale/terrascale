using System.Net;
using System.Text.Json;
using TerraScale.Dashboard.Platform.TsAuth;

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

        [Test]
        public async Task ADatabaseCreatedFromTheCliInAProjectMadeOutsideTheDashboardIsListedEverywhere()
        {
            await using var scenario = await DatabaseSdkScenario.StartAsync(fixture.Root);
            using var client = scenario.CreateClient();
            using var producer = scenario.CreateProviderClient();
            var account = await DatabaseSdkJourneyTests.NativeWorkspaceAsync(client);
            var identity = await IdentityAsync(client);
            var session = await ReadAsync(client, "/api/v1/auth/me");
            var userId = session.GetProperty("userId").GetString()!;

            // As `ts projects create`: ts-auth holds the project; the dashboard never recorded a placement preference.
            TsAuthPlatformProject remote;
            lock (scenario.Workspace.Gate)
            {
                remote = scenario.Workspace.CreateProjectAsync(scenario.Session(userId, account.Email), identity.OrganizationId, "cli-project", CancellationToken.None).GetAwaiter().GetResult();
            }

            // As `ts -project p db create main`: TerraBase alone holds the database, homed wherever its create landed.
            await Control(producer, new { command = "external-create", projectId = remote.Id, value = "main" });
            var observations = await ReadAsync(producer, "/__test/observations");
            var created = observations.GetProperty("databases").EnumerateArray().Single(database => database.GetProperty("name").GetString() == "main");

            // The dashboard session selects the CLI-made project, as the CLI passes -project p for every call.
            var switchToken = await HttpJourney.CsrfAsync(client, "/databases");
            using (var switched = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/switch-project",
                new() { ["_csrf"] = switchToken, ["projectId"] = remote.Id, ["returnUrl"] = "/databases" }))
            {
                await Assert.That(switched.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
            }

            // The list API imports it from TerraBase's listing without any dashboard write having seen it.
            var listed = await ManagementListJourney.ReadListAllAsync(client, "/api/v1/dashboard/databases");
            await Assert.That(listed.GetArrayLength()).IsEqualTo(1);
            await Assert.That(listed[0].GetProperty("id").GetString()).IsEqualTo(created.GetProperty("id").GetString());
            await Assert.That(listed[0].GetProperty("name").GetString()).IsEqualTo("main");
            using (var page = await client.GetAsync("/databases"))
            {
                await Assert.That(await page.Content.ReadAsStringAsync()).Contains("main");
            }

            // The same catalog is the API-key database dropdown, which no prior /databases visit synced.
            using (var keys = await client.GetAsync("/api-keys?create=key"))
            {
                var html = await keys.Content.ReadAsStringAsync();
                await Assert.That(keys.StatusCode).IsEqualTo(HttpStatusCode.OK);
                await Assert.That(html).Contains("<option value=\"" + created.GetProperty("id").GetString() + "\">main</option>");
            }

            // MCP with a PAT lists and reads it, and the delete works without a prior dashboard import.
            var secret = await HttpJourney.CreatePatAsync(client, "CLI visibility", account.Password);
            var mcp = await CallAsync(client, secret, identity.OrganizationId, remote.Id, "list_databases", new());
            await Assert.That(mcp.GetProperty("databases").EnumerateArray().Single(database => database.GetProperty("name").GetString() == "main")
                .GetProperty("id").GetString()).IsEqualTo(created.GetProperty("id").GetString());
            var detail = await CallAsync(client, secret, identity.OrganizationId, remote.Id, "get_database", new() { ["databaseId"] = created.GetProperty("id").GetString() });
            await Assert.That(detail.GetProperty("name").GetString()).IsEqualTo("main");
            var deleted = await CallAsync(client, secret, identity.OrganizationId, remote.Id, "delete_database",
                new() { ["databaseId"] = created.GetProperty("id").GetString(), ["confirmName"] = "main" });
            await Assert.That(deleted.GetProperty("deleted").GetBoolean()).IsTrue();
            await Assert.That((await ManagementListJourney.ReadListAllAsync(client, "/api/v1/dashboard/databases")).GetArrayLength()).IsEqualTo(0);
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

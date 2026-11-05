using System.Net;
using System.Text.Json;
using TerraScale.Dashboard.E2E.Features.Databases;

namespace TerraScale.Dashboard.E2E.Features.Backups
{
    // list_backups and list_databases return one bounded page per call (TS-1219 slice 1).
    [ClassDataSource<DashboardRootFixture>]
    public sealed class BackupPaginationJourneyTests(DashboardRootFixture fixture)
    {
        [Test]
        [Category("Slow")]
        public async Task HappyPagesTraverseBackupsAndDatabasesExactlyOnce()
        {
            await using var host = await DatabaseSdkScenario.StartAsync(fixture.Root);
            using var client = host.CreateClient();
            using var producer = host.CreateProviderClient();
            using var replica = host.CreateClient(replica: true);
            var account = await DatabaseJourney.NativeWorkspaceAsync(client);
            using var login = await HttpJourney.LoginAsync(replica, account.Email, account.Password);
            await Assert.That(login.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
            using (var identity = await client.GetAsync("/api/v1/auth/me"))
            {
                // The free plan admits only one database; the traversal needs three concurrent ones.
                await ControlAsync(producer, new { command = "plan", organizationId = (await JsonAsync(identity)).GetProperty("organizationId").GetString(), value = OrganizationRecord.StarterPlanId });
            }

            var secret = await HttpJourney.CreatePatAsync(client, "Pagination happy path", account.Password);
            var csrf = await HttpJourney.CsrfAsync(client, "/databases");
            var databases = new List<(string Id, string Name)>();
            for (var index = 0; index < 3; index++)
            {
                using var created = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/databases", new()
                { ["_csrf"] = csrf, ["name"] = "pagination-db-" + index, ["region"] = "GRU" });
                await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created);
                databases.Add(((await JsonAsync(created)).GetProperty("id").GetString()!, "pagination-db-" + index));
            }

            var snapshots = new List<string>();
            for (var index = 0; index < 3; index++)
            {
                using var backup = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/backups", new() { ["_csrf"] = csrf, ["databaseId"] = databases[0].Id });
                await Assert.That(backup.StatusCode).IsEqualTo(HttpStatusCode.Created);
                snapshots.Add((await JsonAsync(backup)).GetProperty("id").GetString()!);
            }

            var seen = new List<string>();
            string? after = null;
            do
            {
                var path = "/api/v1/dashboard/backups?databaseId=" + databases[0].Id + "&limit=1" +
                    (after is null ? "" : "&after=" + Uri.EscapeDataString(after));
                using var response = await replica.GetAsync(path);
                await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
                var page = await JsonAsync(response);
                seen.AddRange(page.GetProperty("backups").EnumerateArray().Select(backup => backup.GetProperty("id").GetString()!));
                after = page.GetProperty("nextCursor").GetString();
                await Assert.That(page.GetProperty("hasMore").GetBoolean()).IsEqualTo(after is not null);
            } while (after is not null);
            await Assert.That(seen).IsEquivalentTo(snapshots).Because("every snapshot appears exactly once across REST pages");

            var traversed = new List<string>();
            after = null;
            do
            {
                var page = await McpPageAsync(replica, secret, "list_backups", new Dictionary<string, object?> { ["databaseId"] = databases[0].Id, ["limit"] = 1, ["after"] = after ?? "" });
                traversed.AddRange(page.GetProperty("backups").EnumerateArray().Select(backup => backup.GetProperty("id").GetString()!));
                after = page.GetProperty("nextCursor").GetString();
                await Assert.That(page.GetProperty("hasMore").GetBoolean()).IsEqualTo(after is not null);
            } while (after is not null);
            await Assert.That(traversed).IsEquivalentTo(snapshots).Because("every snapshot appears exactly once across MCP pages");

            traversed.Clear();
            after = null;
            do
            {
                var page = await McpPageAsync(client, secret, "list_databases", new Dictionary<string, object?> { ["limit"] = 2, ["after"] = after ?? "" });
                var ids = page.GetProperty("databases").EnumerateArray().Select(database => database.GetProperty("id").GetString()!).ToArray();
                await Assert.That(ids.Length).IsLessThanOrEqualTo(2);
                traversed.AddRange(ids);
                after = page.GetProperty("nextCursor").GetString();
                await Assert.That(page.GetProperty("hasMore").GetBoolean()).IsEqualTo(after is not null);
            } while (after is not null);
            await Assert.That(traversed).IsEquivalentTo(databases.Select(database => database.Id));
            await Assert.That(traversed.All(id => traversed.Count(other => other == id) == 1)).IsTrue();

            foreach (var (id, name) in databases)
            {
                await CleanupAsync(client, csrf, id, name);
            }
        }

        [Test]
        public async Task MalformedCursorsAndLimitsAreRejected()
        {
            await using var host = await DatabaseSdkScenario.StartAsync(fixture.Root);
            using var client = host.CreateClient();
            var account = await DatabaseJourney.NativeWorkspaceAsync(client);
            var secret = await HttpJourney.CreatePatAsync(client, "Pagination critical path", account.Password);
            var csrf = await HttpJourney.CsrfAsync(client, "/databases");
            using var created = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/databases", new()
            { ["_csrf"] = csrf, ["name"] = "pagination-critical", ["region"] = "GRU" });
            await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created);
            var databaseId = (await JsonAsync(created)).GetProperty("id").GetString()!;

            using (var malformed = await client.GetAsync("/api/v1/dashboard/backups?databaseId=" + databaseId + "&limit=1&after=not-a-cursor"))
            {
                await Assert.That(malformed.StatusCode).IsEqualTo(HttpStatusCode.BadRequest).Because("a malformed producer cursor passes TerraBase's refusal through");
            }

            using (var invalidLimit = await client.GetAsync("/api/v1/dashboard/backups?databaseId=" + databaseId + "&limit=0"))
            {
                await Assert.That(invalidLimit.StatusCode).IsEqualTo(HttpStatusCode.UnprocessableEntity);
            }

            var foreign = await HttpJourney.McpAsync(client, secret, "tools/call", new
            { name = "list_databases", arguments = new { limit = 2, after = "1.broken.wrong" } });
            await Assert.That(foreign.GetProperty("result").GetProperty("isError").GetBoolean()).IsTrue();
            await Assert.That(foreign.GetRawText()).Contains("cursor");
            await CleanupAsync(client, csrf, databaseId, "pagination-critical");
        }

        private static async Task<JsonElement> McpPageAsync(HttpClient client, string secret, string name, Dictionary<string, object?> arguments)
        {
            var response = await HttpJourney.McpAsync(client, secret, "tools/call", new { name, arguments });
            var result = response.GetProperty("result");
            return (result.TryGetProperty("structuredContent", out var page) ? page :
                throw new InvalidOperationException("MCP tool " + name + " failed: " + response.GetRawText())).Clone();
        }

        private static async Task ControlAsync(HttpClient client, object command)
        { using var response = await client.PostAsJsonAsync("/__test/control", command); await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NoContent).Because(await response.Content.ReadAsStringAsync()); }

        private static async Task<JsonElement> JsonAsync(HttpResponseMessage response)
        {
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return document.RootElement.Clone();
        }

        private static async Task CleanupAsync(HttpClient client, string csrf, string databaseId, string name)
        {
            using var deleted = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/databases/delete", new()
            { ["_csrf"] = csrf, ["databaseId"] = databaseId, ["confirmName"] = name });
            await Assert.That(deleted.StatusCode).IsEqualTo(HttpStatusCode.NoContent).Because("cleanup removes the created database: " + deleted.StatusCode);
        }
    }
}

using System.Net;
using System.Text.Json;
using TerraScale.Dashboard.E2E.Features.Databases;

namespace TerraScale.Dashboard.E2E.Features.Backups
{
    // list_backups returns one bounded producer page per call, continuing with the producer's own cursor.
    [ClassDataSource<DashboardRootFixture>]
    public sealed class BackupPaginationJourneyTests(DashboardRootFixture fixture)
    {
        [Test]
        public async Task BackupPagesTraverseEverySnapshotExactlyOnceAndARepeatedCursorEndsTheList()
        {
            await using var host = await DatabaseSdkScenario.StartAsync(fixture.Root);
            using var client = host.CreateClient();
            using var replica = host.CreateClient(replica: true);
            var account = await DatabaseSdkJourneyTests.NativeWorkspaceAsync(client);
            using var login = await HttpJourney.LoginAsync(replica, account.Email, account.Password);
            await Assert.That(login.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
            var secret = await HttpJourney.CreatePatAsync(client, "Backup pagination E2E", account.Password);
            var csrf = await HttpJourney.CsrfAsync(client, "/databases");
            using var created = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/databases", new()
            { ["_csrf"] = csrf, ["name"] = "pagination-backups", ["region"] = "GRU" });
            await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created);
            var databaseId = (await JsonAsync(created)).GetProperty("id").GetString()!;
            var expected = new List<string>();
            for (var index = 0; index < 3; index++)
            {
                using var snapshot = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/backups", new() { ["_csrf"] = csrf, ["databaseId"] = databaseId });
                await Assert.That(snapshot.StatusCode).IsEqualTo(HttpStatusCode.Created);
                expected.Add((await JsonAsync(snapshot)).GetProperty("id").GetString()!);
            }

            expected.Reverse();
            var seen = await TraverseRestAsync(replica, databaseId);
            await Assert.That(seen).IsEquivalentTo(expected).Because("every snapshot appears exactly once across pages");
            var path = "/api/v1/dashboard/backups?databaseId=" + databaseId;
            using (var malformed = await replica.GetAsync(path + "&limit=1&after=not-a-cursor"))
            {
                await Assert.That(malformed.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
            }

            using (var invalidLimit = await replica.GetAsync(path + "&limit=0"))
            {
                await Assert.That(invalidLimit.StatusCode).IsEqualTo(HttpStatusCode.UnprocessableEntity);
            }

            var traversed = new List<string>();
            string? after = null;
            do
            {
                var page = await McpPageAsync(replica, secret, "list_backups", new Dictionary<string, object?> { ["databaseId"] = databaseId, ["limit"] = 1, ["after"] = after ?? "" });
                traversed.AddRange(page.GetProperty("backups").EnumerateArray().Select(backup => backup.GetProperty("id").GetString()!));
                after = page.GetProperty("nextCursor").GetString();
                await Assert.That(page.GetProperty("hasMore").GetBoolean()).IsEqualTo(after is not null);
                await Assert.That(page.GetProperty("policy").GetProperty("retentionDays").GetInt32()).IsEqualTo(7);
            } while (after is not null);
            await Assert.That(traversed).IsEquivalentTo(expected);
            using var repeated = await replica.GetAsync(path + "&limit=1&after=" + Uri.EscapeDataString("backup-cursor:0"));
            await Assert.That(repeated.StatusCode).IsEqualTo(HttpStatusCode.OK);
            var resumed = await JsonAsync(repeated);
            await Assert.That(resumed.GetProperty("nextCursor").GetString()).StartsWith("backup-cursor:")
                .Because("the producer's own cursor keeps flowing through the dashboard response");

            await CleanupAsync(client, csrf, databaseId, "pagination-backups");
        }

        [Test]
        public async Task DatabasesPageOverTheMergedListAfterEveryRegionSync()
        {
            await using var host = await DatabaseSdkScenario.StartAsync(fixture.Root);
            using var client = host.CreateClient();
            using var producer = host.CreateProviderClient();
            var account = await DatabaseSdkJourneyTests.NativeWorkspaceAsync(client);
            using var login = await HttpJourney.LoginAsync(client, account.Email, account.Password);
            await Assert.That(login.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
            // The free plan admits only one database; the traversal needs three concurrent ones.
            using (var identity = await client.GetAsync("/api/v1/auth/me"))
            {
                await ControlAsync(producer, new { command = "plan", organizationId = (await JsonAsync(identity)).GetProperty("organizationId").GetString(), value = OrganizationRecord.StarterPlanId });
            }

            var secret = await HttpJourney.CreatePatAsync(client, "Database pagination E2E", account.Password);
            var csrf = await HttpJourney.CsrfAsync(client, "/databases");
            var expected = new List<string>();
            for (var index = 0; index < 3; index++)
            {
                using var created = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/databases", new()
                { ["_csrf"] = csrf, ["name"] = "pagination-db-" + index, ["region"] = "GRU" });
                await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created);
                expected.Add((await JsonAsync(created)).GetProperty("id").GetString()!);
            }

            var traversed = new List<string>();
            string? after = null;
            do
            {
                var page = await McpPageAsync(client, secret, "list_databases", new Dictionary<string, object?> { ["limit"] = 2, ["after"] = after ?? "" });
                var ids = page.GetProperty("databases").EnumerateArray().Select(database => database.GetProperty("id").GetString()!).ToArray();
                await Assert.That(ids.Length).IsLessThanOrEqualTo(2);
                traversed.AddRange(ids);
                after = page.GetProperty("nextCursor").GetString();
                await Assert.That(page.GetProperty("hasMore").GetBoolean()).IsEqualTo(after is not null);
            } while (after is not null);
            await Assert.That(traversed).IsEquivalentTo(expected);
            await Assert.That(traversed.All(id => traversed.Count(other => other == id) == 1)).IsTrue();
            var foreign = await McpCallAsync(client, secret, "list_databases", new Dictionary<string, object?> { ["limit"] = 2, ["after"] = "1.broken.wrong" });
            await Assert.That(foreign.GetProperty("result").GetProperty("isError").GetBoolean()).IsTrue();
            await Assert.That(foreign.GetRawText()).Contains("cursor");
            foreach (var (id, index) in expected.Select((id, index) => (id, index)))
            {
                await CleanupAsync(client, csrf, id, "pagination-db-" + index);
            }
        }

        private static async Task<List<string>> TraverseRestAsync(HttpClient client, string databaseId)
        {
            var seen = new List<string>();
            string? after = null;
            while (true)
            {
                var path = "/api/v1/dashboard/backups?databaseId=" + databaseId + "&limit=1" +
                    (after is null ? "" : "&after=" + Uri.EscapeDataString(after));
                using var response = await client.GetAsync(path);
                await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
                var page = await JsonAsync(response);
                foreach (var backup in page.GetProperty("backups").EnumerateArray())
                {
                    seen.Add(backup.GetProperty("id").GetString()!);
                }

                after = page.GetProperty("nextCursor").GetString();
                await Assert.That(page.GetProperty("hasMore").GetBoolean()).IsEqualTo(after is not null);
                if (after is null)
                {
                    return seen;
                }
            }
        }

        private static async Task<JsonElement> McpPageAsync(HttpClient client, string secret, string name, Dictionary<string, object?> arguments)
        {
            var response = await HttpJourney.McpAsync(client, secret, "tools/call", new { name, arguments });
            var result = response.GetProperty("result");
            return (result.TryGetProperty("structuredContent", out var page) ? page :
                throw new InvalidOperationException("MCP tool " + name + " failed: " + response.GetRawText())).Clone();
        }

        private static async Task<JsonElement> McpCallAsync(HttpClient client, string secret, string name, Dictionary<string, object?> arguments)
        {
            return (await HttpJourney.McpAsync(client, secret, "tools/call", new { name, arguments })).Clone();
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

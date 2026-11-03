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
            var path = "/api/v1/dashboard/backups?databaseId=" + databaseId;
            var seen = await TraverseRestAsync(replica, path + "&limit=1");
            await Assert.That(seen).IsEquivalentTo(expected).Because("every snapshot appears exactly once across pages");
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
                var page = await McpAsync(replica, secret, "list_backups", new Dictionary<string, object?> { ["databaseId"] = databaseId, ["limit"] = 1, ["after"] = after ?? "" });
                traversed.AddRange(page.GetProperty("backups").EnumerateArray().Select(backup => backup.GetProperty("id").GetString()!));
                after = page.GetProperty("nextCursor").GetString();
                await Assert.That(page.GetProperty("hasMore").GetBoolean()).IsEqualTo(after is not null);
                await Assert.That(page.GetProperty("policy").GetProperty("retentionDays").GetInt32()).IsEqualTo(7);
            } while (after is not null);
            await Assert.That(traversed).IsEquivalentTo(expected);
            using var repeated = await replica.GetAsync(path + "&limit=1&after=" + Uri.EscapeDataString("backup-cursor:0"));
            await Assert.That(repeated.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That((await JsonAsync(repeated)).GetRawText().Contains("backup-cursor:0", StringComparison.Ordinal)).IsTrue()
                .Because("the producer's own cursor keeps flowing through the dashboard response");

            await CleanupAsync(client, csrf, databaseId);
        }

        [Test]
        public async Task DatabasesPageOverTheMergedListAfterEveryRegionSync()
        {
            await using var host = await DatabaseSdkScenario.StartAsync(fixture.Root);
            using var client = host.CreateClient();
            var account = await DatabaseSdkJourneyTests.NativeWorkspaceAsync(client);
            using var login = await HttpJourney.LoginAsync(client, account.Email, account.Password);
            await Assert.That(login.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
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
                var page = await McpAsync(client, secret, "list_databases", new Dictionary<string, object?> { ["limit"] = 2, ["after"] = after ?? "" });
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
            foreach (var id in expected)
            {
                await CleanupAsync(client, csrf, id);
            }
        }

        private static async Task<List<string>> TraverseRestAsync(HttpClient client, string path)
        {
            var seen = new List<string>();
            while (true)
            {
                using var response = await client.GetAsync(path);
                await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
                var page = await JsonAsync(response);
                foreach (var backup in page.GetProperty("backups").EnumerateArray())
                {
                    seen.Add(backup.GetProperty("id").GetString()!);
                }

                var next = page.GetProperty("nextCursor").GetString();
                await Assert.That(page.GetProperty("hasMore").GetBoolean()).IsEqualTo(next is not null);
                if (next is null)
                {
                    return seen;
                }

                path = "/api/v1/dashboard/backups?databaseId=" + path.Split("databaseId=")[1].Split('&')[0] + "&limit=1&after=" + Uri.EscapeDataString(next);
            }
        }

        private static async Task<JsonElement> McpAsync(HttpClient client, string secret, string name, Dictionary<string, object?> arguments)
        {
            var response = await HttpJourney.McpAsync(client, secret, "tools/call", new { name, arguments });
            return response.GetProperty("result").GetProperty("content")[0].GetProperty("structuredContent").Clone();
        }

        private static async Task<JsonElement> McpCallAsync(HttpClient client, string secret, string name, Dictionary<string, object?> arguments)
        {
            return (await HttpJourney.McpAsync(client, secret, "tools/call", new { name, arguments })).Clone();
        }

        private static async Task<JsonElement> JsonAsync(HttpResponseMessage response)
        {
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return document.RootElement.Clone();
        }

        private static async Task CleanupAsync(HttpClient client, string csrf, string databaseId)
        {
            using var detail = await client.GetAsync("/api/v1/dashboard/databases/" + databaseId);
            var name = (await JsonAsync(detail)).GetProperty("name").GetString()!;
            using var deleted = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/databases/delete", new()
            { ["_csrf"] = csrf, ["databaseId"] = databaseId, ["confirmName"] = name });
            await Assert.That(deleted.StatusCode).IsEqualTo(HttpStatusCode.NoContent).Because("cleanup removes the created database: " + deleted.StatusCode);
        }
    }
}

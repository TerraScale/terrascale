using System.Globalization;
using System.Net;
using System.Text.Json;
using TUnit.Core.Enums;

namespace TerraScale.Dashboard.E2E.Features.Databases
{
    [ClassDataSource<DashboardRootFixture>]
    public sealed class AgentDatabaseJourneyTests(DashboardRootFixture fixture)
    {
        [ExecutionPriority(Priority.High)]
        [Test]
        public async Task AgentsCreateForkAtNowAndAtSnapshotQuerySetClearTtlAndDelete()
        {
            await using var scenario = await DatabaseSdkScenario.StartAsync(fixture.Root);
            using var client = scenario.CreateClient();
            using var replica = scenario.CreateClient(replica: true);
            using var producer = scenario.CreateProviderClient();
            var account = await DatabaseJourney.NativeWorkspaceAsync(client);
            using var replicaLogin = await HttpJourney.LoginAsync(replica, account.Email, account.Password);
            var secret = await HttpJourney.CreatePatAsync(client, "Agent databases", account.Password);
            var source = await CallAsync(client, secret, "create_database", new { name = "agent-source", ttlHours = 1.5m });
            var id = source.GetProperty("id").GetString()!;
            var expires = source.GetProperty("expiresAt").GetDateTimeOffset();
            await Assert.That((expires - source.GetProperty("createdAt").GetDateTimeOffset()).TotalSeconds).IsBetween(5399d, 5401d);
            using var identity = await client.GetFromJsonAsync<JsonDocument>("/api/v1/auth/me");
            using var plan = await producer.PostAsJsonAsync("/__test/control", new { command = "plan", organizationId = identity!.RootElement.GetProperty("organizationId").GetString(), value = OrganizationRecord.StarterPlanId });
            plan.EnsureSuccessStatusCode();
            var csrf = await HttpJourney.CsrfAsync(client, "/databases/" + id + "/sql");
            await SqlAsync("CREATE TABLE notes (text TEXT)");
            await SqlAsync("INSERT INTO notes VALUES ('snapshot')");
            var backup = await CallAsync(client, secret, "create_backup", new { databaseId = id });
            await SqlAsync("UPDATE notes SET text = 'current'");
            var fork = await CallAsync(client, secret, "fork_database", new { databaseId = id, name = "agent-current", ttlHours = 2m });
            var forkId = fork.GetProperty("id").GetString()!;
            await Assert.That(fork.GetProperty("expiresAt").ValueKind).IsEqualTo(JsonValueKind.String);
            await QueryAsync(forkId, "current");
            await CallAsync(client, secret, "delete_database", new { databaseId = forkId, confirmName = "agent-current" });
            var historical = await CallAsync(replica, secret, "fork_database", new { databaseId = id, name = "agent-history", pointInTime = backup.GetProperty("createdAt").GetString(), ttlHours = 3m });
            await Assert.That(historical.GetProperty("operationId").ValueKind).IsEqualTo(JsonValueKind.String);
            await Assert.That(historical.GetProperty("name").GetString()).IsEqualTo("agent-history");
            var settled = await CallAsync(replica, secret, "get_restore_status", new { databaseId = id, operationId = historical.GetProperty("operationId").GetString()! });
            await Assert.That(settled.GetProperty("status").GetString()).IsEqualTo("completed");
            var historicalId = settled.GetProperty("targetDatabaseId").GetString()!;
            var restored = await CallAsync(replica, secret, "get_database", new { databaseId = historicalId });
            await Assert.That(restored.GetProperty("name").GetString()).IsEqualTo("agent-history");
            await Assert.That(restored.GetProperty("expiresAt").ValueKind).IsEqualTo(JsonValueKind.String);
            await QueryAsync(historicalId, "snapshot");
            await QueryAsync(id, "current");
            var updated = await CallAsync(replica, secret, "set_database_ttl", new { databaseId = id, ttlHours = 24m });
            await Assert.That(updated.GetProperty("expiresAt").GetDateTimeOffset()).IsGreaterThan(expires);
            var readback = await CallAsync(client, secret, "get_database", new { databaseId = id });
            await Assert.That(readback.GetProperty("expiresAt").GetString()).IsEqualTo(updated.GetProperty("expiresAt").GetString());
            using var page = await client.GetAsync("/databases");
            await Assert.That(await page.Content.ReadAsStringAsync()).Contains(updated.GetProperty("expiresAt").GetDateTimeOffset().UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture));
            using var cleared = await HttpJourney.PostFormAsync(client, "/v1/databases/ttl", new()
            { ["_csrf"] = await HttpJourney.CsrfAsync(client), ["databaseId"] = id, ["action"] = "clear" });
            await Assert.That(cleared.StatusCode).IsEqualTo(HttpStatusCode.OK);
            readback = await CallAsync(replica, secret, "get_database", new { databaseId = id });
            await Assert.That(readback.GetProperty("expiresAt").ValueKind).IsEqualTo(JsonValueKind.Null);
            await CallAsync(client, secret, "set_database_ttl", new { databaseId = historicalId, clear = true });
            await CallAsync(client, secret, "delete_database", new { databaseId = historicalId, confirmName = "agent-history" });
            await CallAsync(client, secret, "delete_database", new { databaseId = id, confirmName = "agent-source" });
            var listing = await CallAsync(replica, secret, "list_databases", new { });
            await Assert.That(listing.GetProperty("databases").GetArrayLength()).IsEqualTo(0);

            async Task SqlAsync(string sql)
            {
                using var response = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/data/sql", new()
                { ["_csrf"] = csrf, ["databaseId"] = id, ["sql"] = sql });
                await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK).Because(sql);
            }

            async Task QueryAsync(string databaseId, string expected)
            {
                var query = await CallAsync(replica, secret, "run_sql", new { databaseId, sql = "SELECT * FROM notes LIMIT 100" });
                await Assert.That(query.GetProperty("result").GetProperty("rows").GetArrayLength()).IsEqualTo(1);
                await Assert.That(query.GetRawText()).Contains(expected);
            }
        }

        [Test]
        public async Task InvalidAgentTtlRequestsPreserveExpiryAndCreateNoDatabase()
        {
            await using var scenario = await DatabaseSdkScenario.StartAsync(fixture.Root);
            using var client = scenario.CreateClient();
            var account = await DatabaseJourney.NativeWorkspaceAsync(client);
            var secret = await HttpJourney.CreatePatAsync(client, "Invalid agent TTL", account.Password);
            var source = await CallAsync(client, secret, "create_database", new { name = "agent-guard", ttlHours = 1m });
            var id = source.GetProperty("id").GetString()!;
            var expiry = source.GetProperty("expiresAt").GetString();
            foreach (var invalid in new object[] { new { databaseId = id, ttlHours = 0m }, new { databaseId = id, ttlHours = 8761m }, new { databaseId = id, ttlHours = 1m, clear = true }, new { databaseId = id } })
            {
                var rpc = await HttpJourney.McpAsync(client, secret, "tools/call", new { name = "set_database_ttl", arguments = invalid });
                await Assert.That(rpc.GetProperty("result").GetProperty("isError").GetBoolean()).IsTrue();
            }
            var csrf = await HttpJourney.CsrfAsync(client);
            using var invalidForm = await HttpJourney.PostFormAsync(client, "/v1/databases/ttl", new()
            { ["_csrf"] = csrf, ["databaseId"] = id, ["ttlHours"] = "", ["action"] = "set" });
            await Assert.That(invalidForm.StatusCode).IsEqualTo(HttpStatusCode.UnprocessableEntity);
            using var invalidCreate = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/databases", new()
            { ["_csrf"] = csrf, ["name"] = "agent-invalid", ["ttlHours"] = "0" });
            await Assert.That(invalidCreate.StatusCode).IsEqualTo(HttpStatusCode.UnprocessableEntity);
            var badFork = await HttpJourney.McpAsync(client, secret, "tools/call", new { name = "fork_database", arguments = new { databaseId = id, name = "agent-invalid", pointInTime = "yesterday" } });
            await Assert.That(badFork.GetProperty("result").GetProperty("isError").GetBoolean()).IsTrue();
            var readback = await CallAsync(client, secret, "get_database", new { databaseId = id });
            await Assert.That(readback.GetProperty("expiresAt").GetString()).IsEqualTo(expiry);
            var inventory = await CallAsync(client, secret, "list_databases", new { });
            await Assert.That(inventory.GetProperty("databases").GetArrayLength()).IsEqualTo(1);
            await CallAsync(client, secret, "delete_database", new { databaseId = id, confirmName = "agent-guard" });
        }

        private static async Task<JsonElement> CallAsync(HttpClient client, string secret, string name, object arguments)
        {
            var rpc = await HttpJourney.McpAsync(client, secret, "tools/call", new { name, arguments });
            var result = rpc.GetProperty("result");
            await Assert.That(result.TryGetProperty("isError", out var error) && error.GetBoolean()).IsFalse().Because(rpc.GetRawText());
            return result.GetProperty("structuredContent");
        }
    }
}

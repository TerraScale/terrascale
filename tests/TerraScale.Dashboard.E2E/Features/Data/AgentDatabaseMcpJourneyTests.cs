using System.Text.Json;
using TerraScale.Dashboard.E2E.Features.Databases;

namespace TerraScale.Dashboard.E2E.Features.Data
{
    [ClassDataSource<DashboardRootFixture>]
    public sealed class AgentDatabaseMcpJourneyTests(DashboardRootFixture fixture)
    {
        [Test]
        public async Task AgentCreatesForksWritesInspectsRestoresAndDeletesThroughMcp()
        {
            await using var scenario = await DatabaseSdkScenario.StartAsync(fixture.Root);
            using var client = scenario.CreateClient();
            await DatabaseJourney.NativeWorkspaceAsync(client);
            using var identity = await client.GetAsync("/api/v1/auth/me");
            using var account = JsonDocument.Parse(await identity.Content.ReadAsStringAsync());
            using var producer = scenario.CreateProviderClient();
            using var plan = await producer.PostAsJsonAsync("/__test/control", new
            { command = "plan", organizationId = account.RootElement.GetProperty("organizationId").GetString(), value = OrganizationRecord.StarterPlanId });
            plan.EnsureSuccessStatusCode();
            var token = await CreateKeyAsync(client);
            var tools = (await HttpJourney.McpAsync(client, token, "tools/list", new { })).GetProperty("result").GetProperty("tools");
            var sqlTool = tools.EnumerateArray().Single(tool => tool.GetProperty("name").GetString() == "execute_sql");
            await Assert.That(sqlTool.GetProperty("annotations").GetProperty("readOnlyHint").GetBoolean()).IsFalse();
            var source = await CallAsync("create_database", new { name = "agent-source", region = "GRU", ttlSeconds = 3600 });
            var sourceId = source.GetProperty("id").GetString()!;
            await Assert.That(source.GetProperty("expiresAt").GetDateTimeOffset() > DateTimeOffset.UtcNow).IsTrue();
            await CallAsync("execute_sql", new { databaseId = sourceId, sql = "CREATE TABLE results (id TEXT PRIMARY KEY, value TEXT)" });
            await CallAsync("execute_sql", new { databaseId = sourceId, sql = "INSERT INTO results VALUES ('one', 'before')" });
            var fork = await CallAsync("branch_database", new { databaseId = sourceId, name = "agent-fork", ttlSeconds = 600 });
            var forkId = fork.GetProperty("id").GetString()!;
            await Assert.That(fork.GetProperty("expiresAt").GetDateTimeOffset() < source.GetProperty("expiresAt").GetDateTimeOffset()).IsTrue();
            var scoped = await CallAsync("create_scoped_key", new { databaseId = forkId, access = "read_write", ttlSeconds = 600 });
            var updated = (await HttpJourney.McpAsync(client, scoped.GetProperty("key").GetString()!, "tools/call", new
            { name = "execute_sql", arguments = new { databaseId = forkId, sql = "UPDATE results SET value = 'after' WHERE id = 'one'" } })).GetProperty("result");
            await Assert.That(updated.GetProperty("structuredContent").GetProperty("rowsAffected").GetInt64()).IsEqualTo(1);
            await Assert.That((await CallAsync("execute_sql", new { databaseId = forkId, sql = "SELECT value FROM results" })).GetProperty("rows")[0][0].GetString()).IsEqualTo("after");
            await Assert.That((await CallAsync("run_sql", new { databaseId = sourceId, sql = "SELECT value FROM results" })).GetProperty("result").GetProperty("rows")[0][0].GetString()).IsEqualTo("before");
            var schema = await CallAsync("get_database_schema", new { databaseId = forkId });
            var table = schema.GetProperty("tables").EnumerateArray().Single(table => table.GetProperty("name").GetString() == "results");
            await Assert.That(table.GetProperty("fields").EnumerateArray().Any(field => field.GetProperty("path").GetString() == "value" && field.GetProperty("type").GetString() == "TEXT")).IsTrue();
            await CallAsync("delete_database", new { databaseId = forkId, confirmName = "agent-fork" });
            var backup = await CallAsync("create_backup", new { databaseId = sourceId });
            var restored = await CallAsync("restore_backup", new { databaseId = sourceId, backupId = backup.GetProperty("id").GetString(), name = "agent-restored" });
            var restoredId = restored.GetProperty("databaseId").GetString()!;
            await Assert.That((await CallAsync("execute_sql", new { databaseId = restoredId, sql = "SELECT value FROM results" })).GetProperty("rows")[0][0].GetString()).IsEqualTo("before");
            await CallAsync("delete_database", new { databaseId = restoredId, confirmName = "agent-restored" });
            await CallAsync("delete_database", new { databaseId = sourceId, confirmName = "agent-source" });
            await Assert.That((await CallAsync("list_databases", new { })).GetProperty("databases").GetArrayLength()).IsEqualTo(0);

            async Task<JsonElement> CallAsync(string name, object arguments)
            {
                var response = (await HttpJourney.McpAsync(client, token, "tools/call", new { name, arguments })).GetProperty("result");
                if (response.TryGetProperty("isError", out var error) && error.GetBoolean())
                {
                    throw new InvalidOperationException(name + ": " + response.GetProperty("content")[0].GetProperty("text").GetString());
                }
                return response.GetProperty("structuredContent");
            }
        }
        [Test]
        public async Task ScopedReadKeyCannotWriteThroughMcp()
        {
            await using var scenario = await DatabaseSdkScenario.StartAsync(fixture.Root);
            using var client = scenario.CreateClient();
            await DatabaseJourney.NativeWorkspaceAsync(client);
            var token = await CreateKeyAsync(client);
            var source = await CallAsync(token, "create_database", new { name = "read-key-source", region = "GRU" });
            var id = source.GetProperty("id").GetString()!;
            await CallAsync(token, "execute_sql", new { databaseId = id, sql = "CREATE TABLE results (id TEXT PRIMARY KEY)" });
            await CallAsync(token, "execute_sql", new { databaseId = id, sql = "INSERT INTO results VALUES ('one')" });
            var key = (await CallAsync(token, "create_scoped_key", new { databaseId = id, access = "read", ttlSeconds = 600 })).GetProperty("key").GetString()!;
            await Assert.That((await CallAsync(key, "run_sql", new { databaseId = id, sql = "SELECT count(*) FROM results" })).GetProperty("result").GetProperty("rows")[0][0].GetInt32()).IsEqualTo(1);
            var refused = (await HttpJourney.McpAsync(client, key, "tools/call", new
            { name = "execute_sql", arguments = new { databaseId = id, sql = "INSERT INTO results VALUES ('two')" } })).GetProperty("result");
            await Assert.That(refused.GetProperty("isError").GetBoolean()).IsTrue();
            await Assert.That(refused.GetProperty("content")[0].GetProperty("text").GetString()).Contains("documents:write");
            await Assert.That((await CallAsync(token, "execute_sql", new { databaseId = id, sql = "SELECT count(*) FROM results" })).GetProperty("rows")[0][0].GetInt32()).IsEqualTo(1);
            await CallAsync(token, "delete_database", new { databaseId = id, confirmName = "read-key-source" });

            async Task<JsonElement> CallAsync(string credential, string name, object arguments) =>
                (await HttpJourney.McpAsync(client, credential, "tools/call", new { name, arguments })).GetProperty("result").GetProperty("structuredContent");
        }

        private static async Task<string> CreateKeyAsync(HttpClient client)
        {
            using var response = await HttpJourney.PostConfirmedFormAsync(client, "/api/v1/dashboard/credentials/key", new()
            {
                ["_csrf"] = await HttpJourney.CsrfAsync(client, "/api-keys"),
                ["name"] = "Agent MCP",
                ["scopes"] = "databases:read,databases:write,documents:read,documents:write",
                ["expiresInDays"] = "0"
            });
            response.EnsureSuccessStatusCode();
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return body.RootElement.GetProperty("secret").GetString()!;
        }
    }
}

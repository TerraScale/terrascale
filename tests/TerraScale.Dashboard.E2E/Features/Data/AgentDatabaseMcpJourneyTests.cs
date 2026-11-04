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
            var native = await DatabaseJourney.NativeWorkspaceAsync(client);
            using var identity = await client.GetAsync("/api/v1/auth/me");
            using var account = JsonDocument.Parse(await identity.Content.ReadAsStringAsync());
            using var producer = scenario.CreateProviderClient();
            using var plan = await producer.PostAsJsonAsync("/__test/control", new
            { command = "plan", organizationId = account.RootElement.GetProperty("organizationId").GetString(), value = OrganizationRecord.StarterPlanId });
            plan.EnsureSuccessStatusCode();
            var token = await HttpJourney.CreatePatAsync(client, "Agent database journey", native.Password);
            var tools = (await HttpJourney.McpAsync(client, token, "tools/list", new { })).GetProperty("result").GetProperty("tools");
            var sqlTool = tools.EnumerateArray().Single(tool => tool.GetProperty("name").GetString() == "execute_sql");
            await Assert.That(sqlTool.GetProperty("annotations").GetProperty("readOnlyHint").GetBoolean()).IsFalse();
            var source = await CallAsync("create_database", new { name = "agent-source", region = "GRU" });
            var sourceId = source.GetProperty("id").GetString()!;
            await CallAsync("execute_sql", new { databaseId = sourceId, sql = "CREATE TABLE results (id TEXT PRIMARY KEY, value TEXT)" });
            await CallAsync("execute_sql", new { databaseId = sourceId, sql = "INSERT INTO results VALUES ('one', 'before')" });
            var fork = await CallAsync("branch_database", new { databaseId = sourceId, name = "agent-fork" });
            var forkId = fork.GetProperty("id").GetString()!;
            await CallAsync("execute_sql", new { databaseId = forkId, sql = "UPDATE results SET value = 'after' WHERE id = 'one'" });
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
    }
}

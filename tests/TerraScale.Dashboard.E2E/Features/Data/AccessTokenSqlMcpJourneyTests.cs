using System.Text.Json;
using TerraScale.Dashboard.E2E.Features.Databases;

namespace TerraScale.Dashboard.E2E.Features.Data
{
    [ClassDataSource<DatabaseMcpFixture>(Shared = SharedType.PerTestSession)]
    public sealed class AccessTokenSqlMcpJourneyTests(DatabaseMcpFixture fixture)
    {
        [Test]
        public async Task PersonalAccessTokenWritesAndReadsThroughMcpSqlTools()
        {
            var scenario = fixture.Scenario;
            using var client = scenario.CreateClient();
            var account = await DatabaseJourney.NativeWorkspaceAsync(client);
            var token = await HttpJourney.CreatePatAsync(client, "MCP SQL E2E", account.Password);
            var databaseId = await HttpJourney.CreateDatabaseAsync(client, "pat-sql-mcp");
            await CallAsync("execute_sql", new { databaseId, sql = "CREATE TABLE t (a INTEGER)" });
            var inserted = await CallAsync("execute_sql", new { databaseId, sql = "INSERT INTO t VALUES (1)" });
            await Assert.That(inserted.GetProperty("rowsAffected").GetInt64()).IsEqualTo(1);
            var selected = await CallAsync("run_sql", new { databaseId, sql = "SELECT a FROM t" });
            await Assert.That(selected.GetProperty("result").GetProperty("rows")[0][0].GetInt32()).IsEqualTo(1);

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

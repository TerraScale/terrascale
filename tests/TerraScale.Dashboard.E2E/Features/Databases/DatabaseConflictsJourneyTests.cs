using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using TerraScale.Dashboard.E2E.Features.Data;

namespace TerraScale.Dashboard.E2E.Features.Databases;

[ClassDataSource<DashboardRootFixture>]
public sealed class DatabaseConflictsJourneyTests(DashboardRootFixture fixture)
{
    [Test]
    public async Task ConflictsUseReadAuthorityAndKeepFiltersAndTransactionOutcome()
    {
        TestAuth auth = null!;
        var statements = new List<string>();
        await using var scenario = await DatabaseSdkScenario.StartAsync(fixture.Root, DatabaseJourney.CaptureAuth(value => auth = value), async http =>
        {
            if (!http.Request.Path.Value!.EndsWith("/sql/execute", StringComparison.Ordinal)) { return null; }
            http.Request.EnableBuffering();
            using var body = await JsonDocument.ParseAsync(http.Request.Body);
            http.Request.Body.Position = 0;
            var sql = body.RootElement.GetProperty("statements")[0].GetProperty("sql").GetString()!;
            if (!sql.Contains("terrascale.conflicts", StringComparison.Ordinal)) { return null; }
            await Assert.That(auth.ResolveProjectToken(http.Request.Headers.Authorization.ToString()[7..])!.Value.Scopes.SequenceEqual(["sql:read"])).IsTrue();
            lock (statements) { statements.Add(sql); }
            var columns = new[] { "id", "kind", "table_name", "row_key", "column_name", "winner_value", "loser_value", "winner_region", "winner_node", "winner_hlc", "winner_transaction_id", "winner_state", "loser_region", "loser_node", "loser_hlc", "loser_transaction_id", "loser_state", "message", "created_at" };
            return Results.Json(new { results = new[] { SqlCatalogJourneyTests.Result(columns,
                [new object?[] { "01M4EQ00000000000000000002", "superseded", "orders'archive", "{\"id\":\"order-1\"}", "amount", "9", "7", "gru", "gru-1", "019a50c1e2f3.00000001:gru-1", "01M4EP00000000000000000003", "confirmed", "sjc", "sjc-1", "019a50c1e2f3.00000000:sjc-1", "01M4EP00000000000000000002", "superseded", "A greater column HLC won.", "2026-11-04T12:00:00.000Z" },
                new object?[] { "01M4EQ00000000000000000001", "foreign_key", "orders'archive", "{\"id\":\"order-2\"}", null, "null", "[{\"op\":\"delete\",\"table\":\"orders'archive\",\"key\":{\"id\":\"order-2\"}}]", null, null, null, null, null, "fra", "fra-1", "019a50c1e2f2.00000000:fra-1", "01M4EP00000000000000000001", "reverted", "A foreign key would be broken.", "2026-11-04T11:00:00.000Z" }]) } });
        });
        using var client = scenario.CreateClient();
        var account = await DatabaseJourney.NativeWorkspaceAsync(client);
        var id = await HttpJourney.CreateDatabaseAsync(client, "conflicts");
        var pat = await HttpJourney.CreatePatAsync(client, "Conflicts", account.Password);
        var path = "/databases/" + id + "/conflicts?table=orders%27archive&since=2026-11-04T00%3A00%3A00&until=2026-11-05T00%3A00%3A00";
        using var page = await client.GetAsync(path);
        await Assert.That(page.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var html = WebUtility.HtmlDecode(await page.Content.ReadAsStringAsync());
        await Assert.That(html).Contains("data-conflict-id=\"01M4EQ00000000000000000002\"");
        await Assert.That(html).Contains("<code>7</code>");
        await Assert.That(html).Contains("<code>null</code>");
        await Assert.That(html).Contains("019a50c1e2f3.00000001:gru-1");
        await Assert.That(html).Contains("confirmed");
        await Assert.That(html).Contains("reverted");
        var rpc = await HttpJourney.McpAsync(client, pat, "tools/call", new { name = "list_conflicts", arguments = new { databaseId = id, table = "orders'archive", since = "2026-11-04T00:00:00Z", until = "2026-11-05T00:00:00Z", limit = 1 } });
        var result = rpc.GetProperty("result").GetProperty("structuredContent");
        await Assert.That(result.GetProperty("conflicts").GetArrayLength()).IsEqualTo(1);
        await Assert.That(result.GetProperty("conflicts")[0].GetProperty("loser").GetProperty("transactionState").GetString()).IsEqualTo("confirmed");
        await Assert.That(result.GetProperty("conflicts")[0].GetProperty("winner").GetProperty("value").GetInt32()).IsEqualTo(9);
        await Assert.That(result.GetProperty("hasMore").GetBoolean()).IsTrue();
        var before = result.GetProperty("nextBefore").GetString()!;
        await HttpJourney.McpAsync(client, pat, "tools/call", new { name = "list_conflicts", arguments = new { databaseId = id, before } });
        await Assert.That(statements[0]).Contains("table_name = 'orders''archive'");
        await Assert.That(statements[0]).Contains("julianday(created_at) >= julianday('");
        await Assert.That(statements[0]).Contains("julianday(created_at) < julianday('");
        await Assert.That(statements[1]).Contains("ORDER BY id DESC LIMIT 2");
        await Assert.That(statements[2]).Contains("id < '" + before + "'");
    }

    [Test]
    public async Task UnavailableConflictsAreAnErrorAndAnotherDatabaseCannotBeRead()
    {
        var reads = 0;
        await using var scenario = await DatabaseSdkScenario.StartAsync(fixture.Root, producerExtension: async http =>
        {
            if (!http.Request.Path.Value!.EndsWith("/sql/execute", StringComparison.Ordinal)) { return null; }
            http.Request.EnableBuffering();
            using var body = await JsonDocument.ParseAsync(http.Request.Body);
            http.Request.Body.Position = 0;
            if (!body.RootElement.GetProperty("statements")[0].GetProperty("sql").GetString()!.Contains("terrascale.conflicts", StringComparison.Ordinal)) { return null; }
            Interlocked.Increment(ref reads);
            return Results.StatusCode(403);
        });
        using var client = scenario.CreateClient();
        var account = await DatabaseJourney.NativeWorkspaceAsync(client);
        var id = await HttpJourney.CreateDatabaseAsync(client, "conflicts-denied");
        var pat = await HttpJourney.CreatePatAsync(client, "Conflict isolation", account.Password);
        using var page = await client.GetAsync("/databases/" + id + "/conflicts");
        var html = await page.Content.ReadAsStringAsync();
        await Assert.That(html).Contains("role=\"alert\"");
        await Assert.That(html).DoesNotContain("No conflicts match");
        var rpc = await HttpJourney.McpAsync(client, pat, "tools/call", new { name = "list_conflicts", arguments = new { databaseId = id } });
        await Assert.That(rpc.GetProperty("result").GetProperty("isError").GetBoolean()).IsTrue();
        var count = Volatile.Read(ref reads);
        using var foreign = await client.GetAsync("/databases/01M4EQ00000000000000000009/conflicts");
        await Assert.That(foreign.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        rpc = await HttpJourney.McpAsync(client, pat, "tools/call", new { name = "list_conflicts", arguments = new { databaseId = "01M4EQ00000000000000000009" } });
        await Assert.That(rpc.GetProperty("result").GetProperty("isError").GetBoolean()).IsTrue();
        await Assert.That(Volatile.Read(ref reads)).IsEqualTo(count);
    }
}

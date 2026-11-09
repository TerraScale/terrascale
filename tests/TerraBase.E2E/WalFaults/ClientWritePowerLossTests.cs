using System.Diagnostics;
using System.Text.Json;
using TerraBase.Primitives;

namespace TerraBase.E2E.WalFaults;

public sealed class ClientWritePowerLossTests
{
    [Test]
    [Category("local-only")]
    [Scenario(Nodes.Processes, Faults.ProcessKill | Faults.FileTampering)]
    public async Task A_power_loss_keeps_every_sql_write_the_node_answered()
    {
        Skip.When(WalSyncFaults.Library is null, "needs Linux and cc to build the write-ahead log fault shim");
        await using var node = await PowerLossNode.StartAsync();
        var createdDatabase = await node.CreateDatabaseAsync(EntityIds.New().ToString(), []);
        var path = $"/v1/projects/{createdDatabase.Project}/databases/canary";
        await ExecuteAsync(node.Client, path, "CREATE TABLE items (id INTEGER PRIMARY KEY, n INTEGER NOT NULL)");
        for (var n = 0; n < 3; n++)
        {
            await ExecuteAsync(node.Client, path, $"INSERT INTO items (n) VALUES ({n})");
        }

        var delay = TimeSpan.FromMilliseconds(500);
        node.DelaySyncs(delay);
        var clock = Stopwatch.StartNew();
        await ExecuteAsync(node.Client, path, "INSERT INTO items (n) VALUES (3)", "INSERT INTO items (n) VALUES (4)");
        await Assert.That(clock.Elapsed).IsGreaterThanOrEqualTo(delay).Because("a write is answered after its commit's sync");
        node.ClearFaults();

        await node.PowerLossAsync();
        await node.StartAgainAsync();

        var rows = await ExecuteAsync(node.Client, path, "SELECT n FROM items ORDER BY n");
        await Assert.That(rows.GetProperty("results")[0].GetProperty("rows").EnumerateArray().Select(row => row[0].GetInt32()))
            .IsEquivalentTo([0, 1, 2, 3, 4]).Because("every write was answered before the power loss");
    }

    private static async Task<JsonElement> ExecuteAsync(HttpClient client, string path, params string[] sql)
    {
        using var response = await client.PostAsJsonAsync(new Uri(path + "/sql/execute", UriKind.Relative),
            new { statements = sql.Select(text => new { sql = text }).ToArray() });
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        await Assert.That(response.IsSuccessStatusCode).IsTrue().Because(body.GetRawText());
        return body;
    }
}

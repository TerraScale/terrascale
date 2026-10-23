using Microsoft.Extensions.DependencyInjection;
using TerraBase.Features.Sql;
using Machine = TerraBase.Fast.Replication.PeerStreamTests.Machine;
using ReplicaCluster = TerraBase.Fast.Replication.PeerStreamTests.Cluster;

namespace TerraBase.Fast.Sql;

public sealed class SqlReplicationTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(60);

    private static async Task<SqlOutcome> RunAsync(Machine machine, string sql)
    {
        var session = machine.Api.Server.Services.GetRequiredService<ISqlService>().Open(machine.Cluster.Database, canWrite: true);
        await using (session)
        {
            return await session.ExecuteAsync(new SqlCommand(sql, [], new Dictionary<string, SqlValue>()), CancellationToken.None);
        }
    }

    private static async Task<string> RowAsync(Machine machine, string sql) => await RunAsync(machine, sql) switch
    {
        SqlResult { Rows: [var row] } => string.Join('|', row.Select(value => value.Kind == SqlValueKind.Text ? value.Text : value.Integer.ToString(System.Globalization.CultureInfo.InvariantCulture))),
        SqlResult => "",
        SqlError error => error.Code,
    };

    private static async Task<bool> UntilAsync(Machine machine, string sql, string expected)
    {
        var deadline = DateTime.UtcNow + Deadline;
        while (DateTime.UtcNow < deadline)
        {
            if (await RowAsync(machine, sql) == expected)
            {
                return true;
            }

            await Task.Delay(100);
        }

        return false;
    }

    [Test]
    public async Task Sql_tables_and_rows_replicate_and_concurrent_increments_add()
    {
        await using var cluster = await ReplicaCluster.StartAsync(cdc: true, "gru", "sjc");
        var (gru, sjc) = (cluster["gru"], cluster["sjc"]);
        const string read = "SELECT n, label FROM counters WHERE id = 1";
        await Assert.That(await RowAsync(gru, "CREATE TABLE counters (id INTEGER PRIMARY KEY, n INTEGER NOT NULL, label TEXT)")).IsEqualTo("");
        await Assert.That(await RowAsync(gru, "INSERT INTO counters (id, n, label) VALUES (1, 0, 'start')")).IsEqualTo("");

        // The definition and the row both reach sjc.
        await Assert.That(await UntilAsync(sjc, read, "0|start")).IsTrue();

        var updates = await Task.WhenAll(
            RunAsync(gru, "UPDATE counters SET n = n + 1, label = 'gru' WHERE id = 1"),
            RunAsync(sjc, "UPDATE counters SET n = n + 1 WHERE id = 1"));

        await Assert.That(updates.All(outcome => outcome is SqlResult { RowsAffected: 1 })).IsTrue();
        // Increments add instead of the last writer winning; the label set on gru survives sjc's concurrent write.
        await Assert.That(await UntilAsync(gru, read, "2|gru")).IsTrue();
        await Assert.That(await UntilAsync(sjc, read, "2|gru")).IsTrue();
    }
}

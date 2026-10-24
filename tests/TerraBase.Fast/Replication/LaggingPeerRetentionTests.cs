using System.Text;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine.Lattices;
using TerraBase.Engine.Turso;
using TerraBase.Primitives;
using TerraBase.Testing;

namespace TerraBase.Fast.Replication;

/// <summary>
/// TS-982: a catalog peer that is still heard from but never acknowledges held every SYSTEM change-log row. The joined rows
/// were small, so the 1 GiB far-behind cut never fired, and the log reached 1.3 GB within hours (2 million rows) while every
/// retention pass stalled the writer. Simulated minutes of catalog churn here (each one ages the log by two minutes, as
/// retention reads time) show the log staying bounded with such a peer, long before the 24 h age cap could act, and also
/// with a continuous-backup anchor that stopped moving: every retention delete stays below that anchor.
/// </summary>
public sealed class LaggingPeerRetentionTests
{
    // Joined catalog rows per simulated minute, in small transactions as replication applies them.
    private const int TransactionsPerMinute = 8;
    private const int RowsPerTransaction = 5_000;
    private const int RowsPerMinute = TransactionsPerMinute * RowsPerTransaction;
    private const int Minutes = 36;

    // TursoStorageTransaction.MaxBehindRows: past this many change ids behind, a peer stops holding rows.
    private const long MaxBehindRows = 1L << 20;

    // A backup tap that cannot read (its anchor stuck at the start) pins the log the same way, with or without the peer's lag.
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task A_lagging_catalog_peer_never_lets_the_system_change_log_grow_past_the_cut(bool backupStuck)
    {
        var token = TestContext.Current!.Execution.CancellationToken;
        await using var server = await TerraBaseTestServer.StartAsync();
        var engine = server.Services.GetRequiredService<TursoStorageEngine>();
        var groups = server.Services.GetRequiredService<TursoChangeGroupSource>();
        var system = (TursoDatabaseStore)await engine.OpenSystemDatabaseAsync(token);
        _ = await RunAsync(system, "CREATE TABLE IF NOT EXISTS lagging_churn (n INTEGER NOT NULL, body BLOB NOT NULL);", token);

        // The peer acknowledged everything so far, then stops: a local catalog write right after its ack stays unacknowledged,
        // so no raise over joined rows moves it either.
        var acked = await ScalarAsync(system, "SELECT max(change_id) FROM turso_cdc;", token);
        var peer = unchecked((long)ReplicaId.Of(2, 7, 0).Value);
        _ = await RunAsync(system, $"INSERT INTO _tb_peers (node, applied, acked, stable, seen, incarnation) VALUES ({peer}, 0, {acked}, 0, {Now()}, '{system.Incarnation}');", token);
        await using (var write = await system.BeginTransactionAsync(write: true, token))
        {
            _ = await write.PutAsync("lagging_probe", EntityIds.New().ToString(), Encoding.UTF8.GetBytes("""{"n":1}"""), token);
            await write.CommitAsync(token);
        }

        var probe = await ScalarAsync(system, "SELECT max(change_id) FROM turso_cdc;", token);
        var aged = probe;
        var largest = 0L;
        var heldBeforeTheCut = false;
        for (var minute = 0; minute < Minutes; minute++)
        {
            for (var transaction = 0; transaction < TransactionsPerMinute; transaction++)
            {
                _ = await RunAsync(system, $"""
                    WITH RECURSIVE pad(n) AS (SELECT 1 UNION ALL SELECT n + 1 FROM pad WHERE n < {RowsPerTransaction})
                    INSERT INTO lagging_churn (n, body) SELECT n, zeroblob(64) FROM pad;
                    """, token);
            }

            // A minute passes for retention (this minute's rows age past the 60 s grace); the peer is heard from (a frontier
            // or an old ack) but its ack does not move.
            _ = await RunAsync(system, $"UPDATE turso_cdc SET change_time = change_time - 120 WHERE change_id > {aged};", token);
            aged = await ScalarAsync(system, "SELECT max(change_id) FROM turso_cdc;", token);
            _ = await RunAsync(system, $"UPDATE _tb_peers SET seen = {Now()} WHERE node = {peer};", token);
            system.BackupAnchor = backupStuck ? probe : null;
            await groups.TrimAsync(system, token);

            var rows = await ScalarAsync(system, "SELECT count(*) FROM turso_cdc;", token);
            largest = Math.Max(largest, rows);
            if ((minute + 1) * (long)RowsPerMinute < MaxBehindRows - RowsPerMinute)
            {
                // Within the cut the lagging peer still holds its rows: it can catch up without a refill.
                heldBeforeTheCut = rows >= (minute + 1) * (long)RowsPerMinute;
                await Assert.That(heldBeforeTheCut).IsTrue().Because($"minute {minute}: {rows} rows kept");
            }
        }

        await Assert.That(await ScalarAsync(system, $"SELECT acked FROM _tb_peers WHERE node = {peer};", token)).IsLessThan(probe)
            .Because("the peer never acknowledged the local write");
        await Assert.That(largest).IsLessThanOrEqualTo(MaxBehindRows + 2L * RowsPerMinute).Because("the cut bounds the log");
        await Assert.That(await ScalarAsync(system, "SELECT count(*) FROM turso_cdc;", token)).IsLessThanOrEqualTo(2L * RowsPerMinute)
            .Because("past the cut each pass keeps only the last minute's rows");
    }

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private static Task<long> ScalarAsync(TursoDatabaseStore store, string sql, CancellationToken token) =>
        store.WithWriterAsync(session =>
        {
            using var command = session.Connection.CreateCommand();
            command.CommandText = sql;
            return (long)command.ExecuteScalar()!;
        }, "a test reading the change log", token);

    private static Task<int> RunAsync(TursoDatabaseStore store, string sql, CancellationToken token) =>
        store.WithWriterAsync(session =>
        {
            using var command = session.Connection.CreateCommand();
            command.CommandText = sql;
            return command.ExecuteNonQuery();
        }, "a test writing catalog churn", token);
}

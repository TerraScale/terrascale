using System.Text;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine.Lattices;
using TerraBase.Engine.Turso;
using TerraBase.Primitives;
using TerraBase.Tenancy;
using TerraBase.Testing;

namespace TerraBase.Fast.Replication;

/// <summary>
/// TS-982: a retention pass runs under the writer every 5 s. Past a peer's ack it walks rows that hold no local group (on
/// SYSTEM, every region's joined catalog writes) a bounded number at a time, so a long log of them is crossed over several
/// passes and never stalls the writes queued behind one pass (on prod: 15-22 s writer holds over 2 million rows).
/// </summary>
public sealed class ChangeLogRetentionScanTests
{
    private const int Transactions = 150;
    private const int RowsPerTransaction = 1_000;

    [Test]
    public async Task An_ack_rises_over_a_long_run_of_joined_rows_a_bounded_step_per_pass()
    {
        var token = TestContext.Current!.Execution.CancellationToken;
        await using var server = await TerraBaseTestServer.StartAsync();
        var engine = server.Services.GetRequiredService<TursoStorageEngine>();
        var groups = server.Services.GetRequiredService<TursoChangeGroupSource>();
        var database = new TenantContext(EntityIds.New().ToString(), PlanTier.Hobby, EntityIds.New().ToString(), EntityIds.New().ToString());
        await engine.CreateDatabaseAsync(database, token);
        var store = (TursoDatabaseStore)(await engine.OpenDatabaseAsync(database, token))!;
        await using (var write = await store.BeginTransactionAsync(write: true, token))
        {
            _ = await write.PutAsync("items", EntityIds.New().ToString(), Encoding.UTF8.GetBytes("""{"n":1}"""), token);
            await write.CommitAsync(token);
        }

        var head = await groups.HeadAsync(database, store.Incarnation, token) ?? throw new InvalidOperationException("the copy refused its own incarnation");
        var peer = unchecked((long)ReplicaId.Of(2, 7, 0).Value);
        _ = await RunAsync(store, $"INSERT INTO _tb_peers (node, applied, acked, stable, seen, incarnation) VALUES ({peer}, 0, {head}, 0, {DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}, '{store.Incarnation}');", token);
        // Many small transactions without a local group, as joined remote writes are: a pass may stop at any of their commits.
        for (var transaction = 0; transaction < Transactions; transaction++)
        {
            _ = await RunAsync(store, $"""
                WITH RECURSIVE pad(n) AS (SELECT 1 UNION ALL SELECT n + 1 FROM pad WHERE n < {RowsPerTransaction})
                INSERT INTO _tb_shape_chunk (handle, offset, end_offset, body, sealed_at)
                SELECT 'pad', {transaction * RowsPerTransaction} + n, n, zeroblob(0), 0 FROM pad;
                """, token);
        }

        var newest = await ScalarAsync(store, "SELECT max(change_id) FROM turso_cdc;", token);
        await Assert.That(newest - head).IsGreaterThan(130_000L).Because("the joined rows are captured");

        await groups.TrimAsync(store, token);
        var first = await AckedAsync(store, peer, token);
        await Assert.That(first).IsGreaterThan(head).Because("the pass raised the ack over joined rows");
        // One pass reads at most 65,536 rows past the ack, then stops at the next commit.
        await Assert.That(first - head).IsLessThanOrEqualTo(65_536L + RowsPerTransaction + 1).Because("one pass walks a bounded run of rows");

        var acked = first;
        for (var pass = 0; pass < 5 && acked < newest; pass++)
        {
            await groups.TrimAsync(store, token);
            acked = await AckedAsync(store, peer, token);
        }

        await Assert.That(acked).IsEqualTo(newest).Because("later passes cross the rest of the joined rows");
    }

    private static Task<long> AckedAsync(TursoDatabaseStore store, long peer, CancellationToken token) =>
        ScalarAsync(store, $"SELECT acked FROM _tb_peers WHERE node = {peer};", token);

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
        }, "a test editing the change log or the peers table", token);
}

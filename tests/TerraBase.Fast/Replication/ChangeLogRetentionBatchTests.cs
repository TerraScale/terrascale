using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine.Turso;
using TerraBase.Primitives;
using TerraBase.Tenancy;
using TerraBase.Testing;

namespace TerraBase.Fast.Replication;

/// <summary>
/// Turso keeps a write transaction's dirty pages in memory until it commits. A retention pass over a long releasable backlog
/// (970k SYSTEM rows after a freeze, one import transaction among them) deleted all of it in one statement and ran the node
/// out of memory on every pass. Each pass now deletes a bounded batch, inside one transaction if it must, and later passes
/// take the rest.
/// </summary>
public sealed class ChangeLogRetentionBatchTests
{
    private const long Rows = 100_000;

    // TursoStorageTransaction.RetentionRows.
    private const long Batch = 16_384;

    [Test]
    public async Task A_long_backlog_goes_a_bounded_batch_per_pass_even_inside_one_transaction()
    {
        var token = TestContext.Current!.Execution.CancellationToken;
        await using var server = await TerraBaseTestServer.StartAsync();
        var engine = server.Services.GetRequiredService<TursoStorageEngine>();
        var groups = server.Services.GetRequiredService<TursoChangeGroupSource>();
        var database = new TenantContext(EntityIds.New().ToString(), PlanTier.Hobby, EntityIds.New().ToString(), EntityIds.New().ToString());
        await engine.CreateDatabaseAsync(database, token);
        var store = (TursoDatabaseStore)(await engine.OpenDatabaseAsync(database, token))!;
        store.BackupAnchor = null;
        // One transaction far longer than a batch, as a catalog import is, then a later commit to cut below.
        _ = await RunAsync(store, $"""
            WITH RECURSIVE pad(n) AS (SELECT 1 UNION ALL SELECT n + 1 FROM pad WHERE n < {Rows})
            INSERT INTO _tb_shape_chunk (handle, offset, end_offset, body, sealed_at)
            SELECT 'pad', n, n, zeroblob(0), 0 FROM pad;
            """, token);
        _ = await RunAsync(store, "INSERT INTO _tb_shape_chunk (handle, offset, end_offset, body, sealed_at) VALUES ('tail', 0, 0, zeroblob(0), 0);", token);
        var newest = await ScalarAsync(store, "SELECT max(change_id) FROM turso_cdc;", token);
        var before = await ScalarAsync(store, $"SELECT count(*) FROM turso_cdc WHERE change_id < {newest};", token);
        await Assert.That(before).IsGreaterThan(Rows).Because("the padded rows are captured");

        await groups.TrimAsync(store, token);
        var after = await ScalarAsync(store, $"SELECT count(*) FROM turso_cdc WHERE change_id < {newest};", token);
        // A background retention pass may run beside this one, and each deletes at most a batch.
        await Assert.That(before - after).IsGreaterThan(0L).Because("a pass releases the oldest rows");
        await Assert.That(before - after).IsLessThanOrEqualTo(2 * Batch).Because("one pass deletes a bounded batch, not the backlog");

        for (var pass = 0; pass < 2 * (Rows / Batch) && after > 1; pass++)
        {
            await groups.TrimAsync(store, token);
            after = await ScalarAsync(store, $"SELECT count(*) FROM turso_cdc WHERE change_id < {newest};", token);
        }

        await Assert.That(after).IsLessThanOrEqualTo(1L).Because("later passes take the rest down to the last commit row");
        await Assert.That(await ScalarAsync(store, "SELECT max(change_id) FROM turso_cdc;", token)).IsEqualTo(newest)
            .Because("the newest row stays");
    }

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
        }, "a test padding the change log", token);
}

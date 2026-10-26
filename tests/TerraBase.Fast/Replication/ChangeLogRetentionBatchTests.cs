using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine.Turso;
using TerraBase.Primitives;
using TerraBase.Tenancy;
using TerraBase.Testing;

namespace TerraBase.Fast.Replication;

/// <summary>
/// Turso keeps a write transaction's dirty pages in memory until it commits. A retention pass over a long releasable backlog
/// (970k SYSTEM rows after a freeze, one import transaction among them) deleted all of it in one statement and ran the node
/// out of memory on every pass. A pass now deletes it in bounded batches, each committed before the next, stopping inside
/// one transaction if it must.
/// </summary>
public sealed class ChangeLogRetentionBatchTests
{
    private const long Rows = 100_000;

    // TursoStorageTransaction.RetentionRows.
    private const long Batch = 16_384;

    [Test]
    public async Task A_long_backlog_goes_in_bounded_batches_even_inside_one_transaction()
    {
        var token = TestContext.Current!.Execution.CancellationToken;
        await using var server = await TerraBaseTestServer.StartAsync();
        var engine = server.Services.GetRequiredService<TursoStorageEngine>();
        var groups = server.Services.GetRequiredService<TursoChangeGroupSource>();
        var database = new TenantContext(EntityIds.New().ToString(), PlanTier.Hobby, EntityIds.New().ToString(), EntityIds.New().ToString());
        await engine.CreateDatabaseAsync(database, token);
        var store = (TursoDatabaseStore)(await engine.OpenDatabaseAsync(database, token))!;
        // A backup anchor at 0 holds every row from background passes; the batches below pass no anchor.
        store.BackupAnchor = 0;
        // One transaction far longer than a batch, as a catalog import is, then a later commit to cut below.
        _ = await RunAsync(store, $"""
            WITH RECURSIVE pad(n) AS (SELECT 1 UNION ALL SELECT n + 1 FROM pad WHERE n < {Rows})
            INSERT INTO _tb_shape_chunk (handle, offset, end_offset, body, sealed_at)
            SELECT 'pad', n, n, zeroblob(0), 0 FROM pad;
            """, token);
        _ = await RunAsync(store, "INSERT INTO _tb_shape_chunk (handle, offset, end_offset, body, sealed_at) VALUES ('tail', 0, 0, zeroblob(0), 0);", token);
        var newest = await ScalarAsync(store, "SELECT max(change_id) FROM turso_cdc;", token);
        var span = await ScalarAsync(store, "SELECT max(change_id) - min(change_id) + 1 FROM turso_cdc;", token);
        await Assert.That(span).IsGreaterThan(Rows).Because("the padded rows are captured");

        // Two batches under one writer: each deletes at most a batch and leaves the rest of the transaction for the next.
        await using (var write = (TursoStorageTransaction)await store.BeginTransactionAsync(write: true, token))
        {
            var (first, firstMore) = await write.TrimBatchAsync(null, DateTimeOffset.UtcNow, token);
            var (second, secondMore) = await write.TrimBatchAsync(null, DateTimeOffset.UtcNow, token);
            _ = await write.CommitAsync(token);
            await Assert.That(span - first).IsGreaterThan(0L).Because("a batch deletes the oldest rows");
            await Assert.That(span - first).IsLessThanOrEqualTo(Batch).Because("a batch deletes at most 16,384 rows");
            await Assert.That(first - second).IsGreaterThan(0L).Because("the next batch continues inside the transaction");
            await Assert.That(first - second).IsLessThanOrEqualTo(Batch).Because("and is bounded too");
            await Assert.That(firstMore && secondMore).IsTrue().Because("the rest of the backlog waits for later batches");
        }

        // A pass takes the rest in further committed batches.
        store.BackupAnchor = null;
        await groups.TrimAsync(store, token);
        await Assert.That(await ScalarAsync(store, $"SELECT count(*) FROM turso_cdc WHERE change_id < {newest};", token)).IsLessThanOrEqualTo(1L)
            .Because("one pass drains the backlog down to the last commit row");
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

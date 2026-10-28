using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine;
using TerraBase.Engine.Turso;
using TerraBase.Primitives;
using TerraBase.Tenancy;
using TerraBase.Testing;

namespace TerraBase.Fast.Replication;

/// <summary>
/// A bounded retention delete may stop inside a transaction (<see cref="ChangeLogRetentionBatchTests"/>), leaving a state row
/// whose document row is gone. A lagging peer's read from below that cut decoded the rest as damage and faulted the copy: on
/// SYSTEM it failed the node closed (gru and fra, 2026-10-28). The read now skips that rest and reports the cut, so the peer
/// refills.
/// </summary>
public sealed class ChangeLogCutInsideTransactionTests
{
    [Test]
    public async Task A_read_from_below_a_cut_inside_a_transaction_reports_the_cut_instead_of_faulting()
    {
        var token = TestContext.Current!.Execution.CancellationToken;
        await using var server = await TerraBaseTestServer.StartAsync();
        var storage = server.Services.GetRequiredService<IStorageEngine>();
        var engine = server.Services.GetRequiredService<TursoStorageEngine>();
        var groups = server.Services.GetRequiredService<TursoChangeGroupSource>();
        var database = new TenantContext(EntityIds.New().ToString(), PlanTier.Hobby, EntityIds.New().ToString(), EntityIds.New().ToString());
        await engine.CreateDatabaseAsync(database, token);
        var store = (TursoDatabaseStore)(await engine.OpenDatabaseAsync(database, token))!;
        // A backup anchor at 0 holds every row from background retention passes.
        store.BackupAnchor = 0;
        var before = await ScalarAsync(store, "SELECT coalesce(max(change_id), 0) FROM turso_cdc;", token);
        await PutAsync(storage, database, "cut", token);
        var firstCommit = await ScalarAsync(store, $"SELECT min(change_id) FROM turso_cdc WHERE change_id > {before} AND change_type = 2;", token);
        await PutAsync(storage, database, "kept", token);

        // What a bounded delete stopping inside the first write leaves: its state row without the document row before it.
        var state = await ScalarAsync(store, $"SELECT min(change_id) FROM turso_cdc WHERE change_id > {before} AND table_name = '_tb_state';", token);
        _ = await RunAsync(store, $"DELETE FROM turso_cdc WHERE change_id < {state};", token);

        var read = await groups.ReadAsync(database, store.Incarnation, before, 16, 1 << 20, token);
        await Assert.That(read).IsNotNull().Because("the copy is not faulted by the cut");
        await Assert.That(read!.Retained).IsFalse().Because("rows past the peer's cursor are gone, so it refills");
        await Assert.That(read.Groups.Count).IsEqualTo(1).Because("only the whole write after the cut is decoded");

        var after = await groups.ReadAsync(database, store.Incarnation, firstCommit, 16, 1 << 20, token);
        await Assert.That(after).IsNotNull();
        await Assert.That(after!.Retained).IsTrue().Because("a cursor at the cut transaction's commit lost nothing");
        await Assert.That(after.Groups.Count).IsEqualTo(1);
    }

    private static async Task PutAsync(IStorageEngine storage, TenantContext database, string id, CancellationToken token)
    {
        var store = (await storage.OpenDatabaseAsync(database, token))!;
        await using var write = await store.BeginTransactionAsync(write: true, token);
        _ = await write.PutAsync("items", id, """{"n":1}"""u8.ToArray(), token);
        _ = await write.CommitAsync(token);
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
        }, "a test cutting the change log", token);
}

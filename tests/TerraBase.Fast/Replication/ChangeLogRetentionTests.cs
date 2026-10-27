using System.Net;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine.Lattices;
using TerraBase.Engine.Turso;
using TerraBase.Fast.Documents;

namespace TerraBase.Fast.Replication;

/// <summary>
/// CDC retention follows the peers that still acknowledge (docs/terrabase/multi-region-replication.md section 7): a peer
/// machine that restarted uncleanly acks under a new epoch of its replica id, and its old epoch's row stops holding rows.
/// </summary>
public sealed class ChangeLogRetentionTests
{
    // Three writes commit at c0 < c1 < c2. Epoch 0 of machine 7 in region 2 acked c0; the machine restarts and its epoch 1
    // acks c2. Machine 8 of region 2 and machine 7 of region 3 acked c1: they still hold the rows from c1 on, until they ack
    // c2 too. Kept, epoch 0's row would hold every row from c0 on for 24 h.
    [Test]
    public async Task A_peers_new_epoch_releases_the_change_log_rows_its_old_epoch_held()
    {
        await using var api = await DocumentApi.StartAsync();
        foreach (var n in new[] { 1, 2, 3 })
        {
            (await api.PutAsync(DocumentApi.NewId(), new { n })).Status.ShouldBe(HttpStatusCode.Created);
        }

        var store = (TursoDatabaseStore)(await api.Server.Services.GetRequiredService<TursoStorageEngine>().OpenDatabaseAsync(await api.TenantOfAsync(), CancellationToken.None))!;
        var commits = await store.WithWriterAsync(session =>
        {
            using var command = session.Connection.CreateCommand();
            command.CommandText = "SELECT change_id FROM turso_cdc WHERE change_type = 2 ORDER BY change_id DESC LIMIT 3;";
            using var reader = command.ExecuteReader();
            List<long> ids = [];
            while (reader.Read())
            {
                ids.Insert(0, reader.GetInt64(0));
            }

            return ids;
        }, "a test reading the change log's commits", CancellationToken.None);
        await Assert.That(commits.Count).IsEqualTo(3);
        var (c0, c1, c2) = (commits[0], commits[1], commits[2]);

        // Past the 60 s every commit is held for, well inside the 24 h a silent peer holds rows.
        var now = DateTimeOffset.UtcNow.AddMinutes(2);
        var old = ReplicaId.Of(2, 7, 0);
        var received = new Hlc(1234);
        await using (var transaction = await store.BeginDurableTransactionAsync(CancellationToken.None))
        {
            await transaction.RecordAppliedAsync(old, received, CancellationToken.None);
            _ = await transaction.CommitAsync(CancellationToken.None);
        }
        await Assert.That(await AckAsync(store, ReplicaId.Of(2, 7, 0), c0, now)).IsEqualTo(c0);
        await Assert.That(await AckAsync(store, ReplicaId.Of(2, 8, 0), c1, now)).IsEqualTo(c0);
        await Assert.That(await AckAsync(store, ReplicaId.Of(3, 7, 0), c1, now)).IsEqualTo(c0);
        await Assert.That(await OldestAsync(store)).IsEqualTo(c0);

        await Assert.That(await AckAsync(store, ReplicaId.Of(2, 7, 1), c2, now)).IsEqualTo(c1).Because("epoch 0 of the machine never acks again");
        await Assert.That(await OldestAsync(store)).IsEqualTo(c1).Because("the other machines' rows still hold c1");

        await Assert.That(await AckAsync(store, ReplicaId.Of(2, 8, 0), c2, now)).IsEqualTo(c1);
        await Assert.That(await AckAsync(store, ReplicaId.Of(3, 7, 0), c2, now)).IsEqualTo(c2);
        await Assert.That(await OldestAsync(store)).IsEqualTo(c2);
        await using var read = await store.BeginTransactionAsync(write: false, CancellationToken.None);
        await Assert.That((await read.GetAppliedAsync(CancellationToken.None))[old]).IsEqualTo(received)
            .Because("a new outgoing ACK must not erase the old epoch's durable received-prefix witness");
    }

    // Production gru, 2026-10-27: an identity no listed machine has any more acked c0 and went silent. Restarts kept the
    // process under 24 h of uptime, so it held every later row: 714k rows, and each write held the writer ~1.6 s. Once every
    // listed machine has been heard (the members list), its row stops holding rows at the next live ack, well inside 24 h.
    [Test]
    public async Task An_identity_no_listed_machine_has_releases_the_change_log_rows_it_held()
    {
        await using var api = await DocumentApi.StartAsync();
        foreach (var n in new[] { 1, 2, 3 })
        {
            (await api.PutAsync(DocumentApi.NewId(), new { n })).Status.ShouldBe(HttpStatusCode.Created);
        }

        var store = (TursoDatabaseStore)(await api.Server.Services.GetRequiredService<TursoStorageEngine>().OpenDatabaseAsync(await api.TenantOfAsync(), CancellationToken.None))!;
        var commits = await CommitsAsync(store);
        var (c0, c2) = (commits[0], commits[2]);
        var now = DateTimeOffset.UtcNow.AddMinutes(2);
        var departed = ReplicaId.Of(3, 9, 0);
        var (sjc, fra) = (ReplicaId.Of(2, 7, 0), ReplicaId.Of(4, 7, 0));
        var received = new Hlc(1234);
        await using (var transaction = await store.BeginDurableTransactionAsync(CancellationToken.None))
        {
            await transaction.RecordAppliedAsync(departed, received, CancellationToken.None);
            _ = await transaction.CommitAsync(CancellationToken.None);
        }

        await Assert.That(await AckAsync(store, departed, c0, now)).IsEqualTo(c0);
        await Assert.That(await AckAsync(store, sjc, c2, now)).IsEqualTo(c0);
        await Assert.That(await AckAsync(store, fra, c2, now, members: null)).IsEqualTo(c0)
            .Because("before every listed machine was heard, a silent identity may still be one of them");
        await Assert.That(await OldestAsync(store)).IsEqualTo(c0);

        await Assert.That(await AckAsync(store, fra, c2, now, members: [sjc, fra])).IsEqualTo(c2)
            .Because("no listed machine is the silent identity any more");
        await Assert.That(await OldestAsync(store)).IsEqualTo(c2);
        await using var read = await store.BeginTransactionAsync(write: false, CancellationToken.None);
        await Assert.That((await read.GetAppliedAsync(CancellationToken.None))[departed]).IsEqualTo(received)
            .Because("retiring its sending cursor keeps the departed identity's durable received prefix");
    }

    private static Task<List<long>> CommitsAsync(TursoDatabaseStore store) =>
        store.WithWriterAsync(session =>
        {
            using var command = session.Connection.CreateCommand();
            command.CommandText = "SELECT change_id FROM turso_cdc WHERE change_type = 2 ORDER BY change_id DESC LIMIT 3;";
            using var reader = command.ExecuteReader();
            List<long> ids = [];
            while (reader.Read())
            {
                ids.Insert(0, reader.GetInt64(0));
            }

            return ids;
        }, "a test reading the change log's commits", CancellationToken.None);

    // Records the peer's ack as an acknowledgement on the stream does, at `now`; the floor every live peer acked.
    private static async Task<long?> AckAsync(TursoDatabaseStore store, ReplicaId peer, long changeId, DateTimeOffset now, IReadOnlyList<ReplicaId>? members = null)
    {
        var transaction = (TursoStorageTransaction)await store.BeginTransactionAsync(write: true, CancellationToken.None);
        await using (transaction)
        {
            var (_, floor) = await transaction.RecordAckedAsync(peer, store.Incarnation, changeId, default, backupAnchor: null, now, members, CancellationToken.None)
                ?? throw new InvalidOperationException("the copy refused its own incarnation");
            _ = await transaction.CommitAsync(CancellationToken.None);
            return floor;
        }
    }

    private static Task<long> OldestAsync(TursoDatabaseStore store) =>
        store.WithWriterAsync(session =>
        {
            using var command = session.Connection.CreateCommand();
            command.CommandText = "SELECT min(change_id) FROM turso_cdc;";
            return (long)command.ExecuteScalar()!;
        }, "a test reading the change log's oldest row", CancellationToken.None);
}

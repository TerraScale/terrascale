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
        await Assert.That(await AckAsync(store, ReplicaId.Of(2, 7, 0), c0, now)).IsEqualTo(c0);
        await Assert.That(await AckAsync(store, ReplicaId.Of(2, 8, 0), c1, now)).IsEqualTo(c0);
        await Assert.That(await AckAsync(store, ReplicaId.Of(3, 7, 0), c1, now)).IsEqualTo(c0);
        await Assert.That(await OldestAsync(store)).IsEqualTo(c0);

        await Assert.That(await AckAsync(store, ReplicaId.Of(2, 7, 1), c2, now)).IsEqualTo(c1).Because("epoch 0 of the machine never acks again");
        await Assert.That(await OldestAsync(store)).IsEqualTo(c1).Because("the other machines' rows still hold c1");

        await Assert.That(await AckAsync(store, ReplicaId.Of(2, 8, 0), c2, now)).IsEqualTo(c1);
        await Assert.That(await AckAsync(store, ReplicaId.Of(3, 7, 0), c2, now)).IsEqualTo(c2);
        await Assert.That(await OldestAsync(store)).IsEqualTo(c2);
    }

    // Records the peer's ack as an acknowledgement on the stream does, at `now`; the floor every live peer acked.
    private static async Task<long?> AckAsync(TursoDatabaseStore store, ReplicaId peer, long changeId, DateTimeOffset now)
    {
        var transaction = (TursoStorageTransaction)await store.BeginTransactionAsync(write: true, CancellationToken.None);
        await using (transaction)
        {
            var (_, floor) = await transaction.RecordAckedAsync(peer, store.Incarnation, changeId, default, backupAnchor: null, now, CancellationToken.None)
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

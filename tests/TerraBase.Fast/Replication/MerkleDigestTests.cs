using System.Buffers.Binary;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine;
using TerraBase.Engine.Lattices;
using TerraBase.Engine.Turso;
using TerraBase.Fast.Documents;
using TerraBase.Testing;
using Turso.Data.Sqlite;

namespace TerraBase.Fast.Replication;

public sealed class MerkleDigestTests
{
    [Test]
    public async Task Committed_updates_rollback_collection_drop_GC_and_forget_keep_bucket_digests_exact()
    {
        var token = TestContext.Current!.Execution.CancellationToken;
        await using var api = await DocumentApi.StartAsync(cancellationToken: token);
        var database = await api.TenantOfAsync(cancellationToken: token);
        var engine = api.Server.Services.GetRequiredService<TursoStorageEngine>();
        var client = (TursoDatabaseStore)(await engine.OpenDatabaseAsync(database, token))!;
        var id = DocumentApi.NewId();
        await Assert.That((await api.PutAsync(id, new { value = 1 }, cancellationToken: token)).Status).IsEqualTo(HttpStatusCode.Created);
        await ExactAsync(client);
        await using (var connection = new SqliteConnection(DocumentApi.ConnectionString(await api.DatabaseFileAsync())))
        {
            await connection.OpenAsync(token);
            var fixtureId = DocumentApi.NewId();
            await StoredStates.WriteAsync(connection, "items", fixtureId, "{\"fixture\":1}");
            await ExactAsync(client);
            await Assert.That(await StoredStates.ReplaceAsync(connection, "items", fixtureId, "{\"fixture\":2}")).IsEqualTo(1);
            await ExactAsync(client);
        }
        await Assert.That((await api.PutAsync(id, new { value = 2 }, cancellationToken: token)).Status).IsEqualTo(HttpStatusCode.OK);
        await ExactAsync(client);
        // One transaction replacing the same document twice folds its old and new digest into the bucket each time.
        await using (var twice = (TursoStorageTransaction)await client.BeginTransactionAsync(write: true, token))
        {
            _ = await twice.PutAsync("items", id, "{\"value\":21}"u8.ToArray(), token);
            await twice.FlushStatesAsync();
            _ = await twice.PutAsync("items", id, "{\"value\":22}"u8.ToArray(), token);
            _ = await twice.CommitAsync(token);
        }
        var committed = await ExactAsync(client);
        await using (var rollback = (TursoStorageTransaction)await client.BeginTransactionAsync(write: true, token))
        {
            _ = await rollback.PutAsync("items", id, "{\"value\":3}"u8.ToArray(), token);
            await rollback.FlushStatesAsync();
        }
        await Assert.That((await ExactAsync(client)).SequenceEqual(committed)).IsTrue();
        await Assert.That((await api.SendAsync(HttpMethod.Delete, api.Document(id), cancellationToken: token)).Status).IsEqualTo(HttpStatusCode.OK);
        await ExactAsync(client);
        var horizon = await HorizonAsync();
        await using (var collect = (TursoStorageTransaction)await client.BeginTransactionAsync(write: true, token))
        {
            await Assert.That(await collect.CollectAsync(horizon, token)).IsEqualTo(1);
            _ = await collect.CommitAsync(token);
        }
        await ExactAsync(client);
        await Assert.That((await api.PutAsync(id, new { value = 4 }, cancellationToken: token)).Status).IsEqualTo(HttpStatusCode.Created);
        horizon = await HorizonAsync();
        await using (var forget = (TursoStorageTransaction)await client.BeginTransactionAsync(write: true, token))
        {
            await Assert.That(await forget.ForgetAsync([("items", id)], horizon, token)).IsEqualTo(1);
            _ = await forget.CommitAsync(token);
        }
        await ExactAsync(client);
        await Assert.That((await api.PutAsync(id, new { value = 5 }, cancellationToken: token)).Status).IsEqualTo(HttpStatusCode.Created);
        await Assert.That((await api.SendAsync(HttpMethod.Delete, api.Collections + "/items", cancellationToken: token)).Status).IsEqualTo(HttpStatusCode.OK);
        await ExactAsync(client);

        var system = engine.SystemStore!;
        await ExactAsync(system);
        await using (var write = await system.BeginDurableTransactionAsync(token))
        {
            _ = await write.CreateCollectionAsync(new CollectionInfo("merkle/purge", 1), token);
            _ = await write.PutAsync("merkle/purge", id, "{\"value\":1}"u8.ToArray(), token);
            _ = await write.CommitAsync(token);
        }
        await ExactAsync(system);
        await using (var purge = await system.BeginDurableTransactionAsync(token))
        {
            await Assert.That(await purge.DeleteCollectionsAsync("merkle/", token)).IsEqualTo(1);
            _ = await purge.CommitAsync(token);
        }
        await ExactAsync(system);

        Task<Hlc> HorizonAsync()
        {
            return client.WithWriterAsync(session =>
            {
                using var command = session.Connection.CreateCommand();
                command.CommandText = "SELECT max(stamp) + 1 FROM _tb_state;";
                return new Hlc((ulong)(long)command.ExecuteScalar()!);
            }, "a Merkle fixture measuring its collection horizon", token);
        }

        async Task<byte[]> ExactAsync(TursoDatabaseStore store)
        {
            await using var read = (TursoStorageTransaction)await store.BeginTransactionAsync(write: false, token);
            var expected = await read.LedgerAsync(create =>
            {
                var bytes = new byte[4096 * 16];
                using var command = create("SELECT bucket, h1, h2 FROM _tb_state;");
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    var slot = bytes.AsSpan((int)reader.GetInt64(0) * 16, 16);
                    BinaryPrimitives.WriteInt64LittleEndian(slot, BinaryPrimitives.ReadInt64LittleEndian(slot) ^ reader.GetInt64(1));
                    BinaryPrimitives.WriteInt64LittleEndian(slot[8..], BinaryPrimitives.ReadInt64LittleEndian(slot[8..]) ^ reader.GetInt64(2));
                }
                return bytes;
            }, token);
            var digest = await read.ReadDigestsAsync(token);
            await Assert.That(digest.SequenceEqual(expected)).IsTrue();
            return digest;
        }
    }
}

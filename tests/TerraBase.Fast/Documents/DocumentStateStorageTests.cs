using System.Buffers.Text;
using System.Net;
using System.Text.Json;
using Turso.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine;
using TerraBase.Engine.Lattices;

namespace TerraBase.Fast.Documents;

public sealed class DocumentStateStorageTests
{
    [Test]
    public async Task A_patch_writes_only_the_fields_it_changes_at_a_new_stamp()
    {
        await using var api = await DocumentApi.StartAsync();
        var id = DocumentApi.NewId();
        (await api.PutAsync(id, new { b = 1, a = "x" })).Status.ShouldBe(HttpStatusCode.Created);
        var put = await ReadAsync(api, id);
        (await api.SendRawAsync(HttpMethod.Patch, api.Document(id), """{"b":2}""", "application/merge-patch+json")).Status.ShouldBe(HttpStatusCode.OK);
        var patched = await ReadAsync(api, id);

        await Assert.That(patched.Body).IsEqualTo("""{"a":"x","b":2}""");
        await Assert.That(patched.Stamp).IsGreaterThan(put.Stamp);
        await Assert.That(patched.State.Fields["a"].Base!.Value.Version.Stamp.Value).IsEqualTo((ulong)put.Stamp);
        await Assert.That(patched.State.Fields["b"].Base!.Value.Version.Stamp.Value).IsEqualTo((ulong)patched.Stamp);
        await Assert.That(patched.State.Clear!.Value.Version.Stamp.Value).IsEqualTo((ulong)put.Stamp);
        await Assert.That(patched.Live).IsTrue();
        await Assert.That(patched.Digest).IsEqualTo(DocumentStateEncoding.Digest(DocumentApi.Collection, id, patched.State));
    }

    [Test]
    public async Task Writes_of_one_document_in_one_transaction_share_one_stamp_and_one_state()
    {
        await using var api = await DocumentApi.StartAsync();
        var id = DocumentApi.NewId();
        (await api.PutAsync(DocumentApi.NewId(), new { n = 0 })).Status.ShouldBe(HttpStatusCode.Created);
        var store = (await api.Server.Services.GetRequiredService<IStorageEngine>().OpenDatabaseAsync(await api.TenantOfAsync(), CancellationToken.None))!;
        long[] versions;
        await using (var write = await store.BeginTransactionAsync(write: true, CancellationToken.None))
        {
            versions =
            [
                await write.PutAsync(DocumentApi.Collection, id, """{"n":1,"keep":true}"""u8.ToArray(), CancellationToken.None),
                await write.PatchAsync(DocumentApi.Collection, id, """{"n":2,"keep":true}"""u8.ToArray(), [Set("n", "2")], CancellationToken.None),
                await write.PatchAsync(DocumentApi.Collection, id, """{"n":2,"keep":true,"m":3}"""u8.ToArray(), [Set("m", "3")], CancellationToken.None),
            ];
            await write.CommitAsync(CancellationToken.None);
        }

        var stored = await ReadAsync(api, id);
        await Assert.That(versions.Distinct()).IsEquivalentTo([stored.Stamp]);

        await Assert.That(stored.Body).IsEqualTo("""{"keep":true,"m":3,"n":2}""");
        await Assert.That(stored.State.Fields.Values.Select(field => field.Base!.Value.Version.Stamp.Value).Distinct())
            .IsEquivalentTo([(ulong)stored.Stamp]);
        await Assert.That(stored.State.Clear!.Value.Version.Stamp.Value).IsEqualTo((ulong)stored.Stamp);
    }

    [Test]
    public async Task A_delete_keeps_a_dead_state_and_a_drop_removes_the_collections_states()
    {
        await using var api = await DocumentApi.StartAsync();
        var id = DocumentApi.NewId();
        (await api.PutAsync(id, new { n = 1 })).Status.ShouldBe(HttpStatusCode.Created);
        (await api.SendAsync(HttpMethod.Delete, api.Document(id))).Status.ShouldBe(HttpStatusCode.OK);
        var deleted = await ReadAsync(api, id);

        await Assert.That(deleted.Body).IsNull();
        await Assert.That(deleted.Live).IsFalse();
        await Assert.That(deleted.State.Clear!.Value.Deleted).IsTrue();
        await Assert.That(deleted.State.Exists).IsFalse();

        (await api.PutAsync(DocumentApi.NewId(), new { n = 2 })).Status.ShouldBe(HttpStatusCode.Created);
        (await api.SendAsync(HttpMethod.Delete, $"{api.Collections}/{DocumentApi.Collection}")).Status.ShouldBe(HttpStatusCode.OK);
        await using var connection = new SqliteConnection(DocumentApi.ConnectionString(await api.DatabaseFileAsync(), readOnly: true));
        await connection.OpenAsync();
        await using var count = connection.CreateCommand();
        count.CommandText = "SELECT count(*) FROM _tb_state WHERE collection = $collection;";
        count.Parameters.AddWithValue("$collection", DocumentApi.Collection);
        await Assert.That((long)(await count.ExecuteScalarAsync())!).IsEqualTo(0);
    }

    [Test]
    public async Task Each_collection_is_a_table_of_its_own_and_reserved_names_are_refused()
    {
        await using var api = await DocumentApi.StartAsync();
        var id = DocumentApi.NewId();
        (await api.PutAsync(id, new { n = 1 })).Status.ShouldBe(HttpStatusCode.Created);
        await using (var connection = new SqliteConnection(DocumentApi.ConnectionString(await api.DatabaseFileAsync(), readOnly: true)))
        {
            await connection.OpenAsync();
            await using var row = connection.CreateCommand();
            row.CommandText = "SELECT doc FROM \"items\" WHERE id = $id;";
            row.Parameters.AddWithValue("$id", id);
            await Assert.That((string?)await row.ExecuteScalarAsync()).IsEqualTo("""{"n":1}""");
        }

        (await api.SendAsync(HttpMethod.Delete, $"{api.Collections}/{DocumentApi.Collection}")).Status.ShouldBe(HttpStatusCode.OK);
        await using (var connection = new SqliteConnection(DocumentApi.ConnectionString(await api.DatabaseFileAsync(), readOnly: true)))
        {
            await connection.OpenAsync();
            await using var table = connection.CreateCommand();
            table.CommandText = "SELECT count(*) FROM sqlite_schema WHERE type = 'table' AND name = 'items';";
            await Assert.That((long)(await table.ExecuteScalarAsync())!).IsEqualTo(0);
        }

        foreach (var name in new[] { "commit_offset", "sqlite_x", "turso_cdc" })
        {
            var refused = await api.SendAsync(HttpMethod.Post, api.Collections, new { name });
            await Assert.That((refused.Status, refused.Code)).IsEqualTo((HttpStatusCode.BadRequest, "reserved_name"));
        }
    }

    private static FieldOperation Set(string field, string json)
    {
        using var value = JsonDocument.Parse(json);
        return new FieldOperation(FieldOperationKind.Set, field, value.RootElement.Clone());
    }

    private static async Task<Stored> ReadAsync(DocumentApi api, string id) => await ReadAsync(await api.DatabaseFileAsync(), id);
    internal static async Task<Stored> ReadAsync(string path, string id)
    {
        await using var connection = new SqliteConnection(DocumentApi.ConnectionString(path, readOnly: true));
        await connection.OpenAsync();
        await using var read = connection.CreateCommand();
        read.CommandText = """
            SELECT s.state, d.doc, s.stamp, s.live, s.h1, s.h2 FROM _tb_state s
            LEFT JOIN "items" d ON d.id = s.id
            WHERE s.collection = $collection AND s.id = $id;
            """;
        read.Parameters.AddWithValue("$collection", DocumentApi.Collection);
        read.Parameters.AddWithValue("$id", id);
        await using var reader = await read.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            throw new InvalidOperationException("the document has no state row");
        }

        var body = reader.IsDBNull(1) ? null : reader.GetString(1);
        var state = DocumentStateEncoding.DecodeStored(Base64Url.DecodeFromChars(reader.GetString(0)), body is null ? [] : System.Text.Encoding.UTF8.GetBytes(body));
        var digest = ((UInt128)unchecked((ulong)reader.GetInt64(4)) << 64) | unchecked((ulong)reader.GetInt64(5));
        return new Stored(state, body, reader.GetInt64(2), reader.GetInt64(3) == 1, digest);
    }

    internal sealed record Stored(DocumentState State, string? Body, long Stamp, bool Live, UInt128 Digest);
}

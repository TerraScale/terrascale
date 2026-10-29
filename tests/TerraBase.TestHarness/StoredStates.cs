using System.Buffers.Text;
using System.Text.Json;
using TerraBase.Engine.Lattices;
using TerraBase.Engine.Turso;
using Turso.Data.Sqlite;

namespace TerraBase.Testing;

public static class StoredStates
{
    public const long Version = 1 << 16;
    public static ReplicaId Replica { get; } = ReplicaId.Of(ushort.MaxValue - 1, 1, 0);
    public static async Task WriteAsync(
        SqliteConnection connection, string collection, string id, string body, long version = Version, SqliteTransaction? transaction = null,
        bool system = false, ReplicaId? origin = null)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (transaction is null)
        {
            await using var owned = (SqliteTransaction)await connection.BeginTransactionAsync();
            await WriteAsync(connection, collection, id, body, version, owned, system, origin);
            await owned.CommitAsync();
            return;
        }

        var replica = origin ?? Replica;
        using var json = JsonDocument.Parse(system ? SystemBody(body) : body, DocumentState.JsonOptions);
        var state = DocumentState.Bottom.Put(new EntryVersion(new Hlc((ulong)version), replica), json.RootElement);
        var digest = DocumentStateEncoding.Digest(collection, id, state);
        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = (system
            ? """
                DELETE FROM documents WHERE collection = $collection AND id = $id;
                INSERT INTO documents (collection, id, body) VALUES ($collection, $id, $body);
                """
            : $"""
                DELETE FROM {TursoStorageTransaction.Table(collection)} WHERE id = $id;
                INSERT INTO {TursoStorageTransaction.Table(collection)} (id, doc) VALUES ($id, $body);
                """) + $"""
            DELETE FROM _tb_state WHERE collection = $collection AND id = $id;
            INSERT INTO _tb_state (collection, id, state, stamp, origin, live, h1, h2)
            VALUES ($collection, $id, $state, {version}, $origin, 1, $h1, $h2);
            """;
        insert.Parameters.AddWithValue("$collection", collection);
        insert.Parameters.AddWithValue("$id", id);
        insert.Parameters.AddWithValue("$body", System.Text.Encoding.UTF8.GetString(state.Materialize()!));
        insert.Parameters.AddWithValue("$state", Base64Url.EncodeToString(DocumentStateEncoding.EncodeStored(state)));
        insert.Parameters.AddWithValue("$origin", unchecked((long)replica.Value));
        insert.Parameters.AddWithValue("$h1", unchecked((long)(ulong)(digest >> 64)));
        insert.Parameters.AddWithValue("$h2", unchecked((long)(ulong)digest));
        await insert.ExecuteNonQueryAsync();
    }

    public static async Task<int> ReplaceAsync(
        SqliteConnection connection, string collection, string? id, string body, SqliteTransaction? transaction = null, bool system = false)
    {
        ArgumentNullException.ThrowIfNull(connection);
        List<(string Id, long Stamp, long Origin)> rows = [];
        await using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = "SELECT id, stamp, origin FROM _tb_state WHERE collection = $collection AND live = 1 AND ($id IS NULL OR id = $id) ORDER BY id;";
            select.Parameters.AddWithValue("$collection", collection);
            select.Parameters.AddWithValue("$id", (object?)id ?? DBNull.Value);
            await using var reader = await select.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                rows.Add((reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2)));
            }
        }

        foreach (var (row, stamp, origin) in rows)
        {
            await WriteAsync(connection, collection, row, body, stamp, transaction, system, new ReplicaId(unchecked((ulong)origin)));
        }

        return rows.Count;
    }

    public static string SystemBody(string body)
    {
        using var json = JsonDocument.Parse(body, DocumentState.JsonOptions);
        return json.RootElement.ValueKind == JsonValueKind.Object ? body : $"{{\"\\u0000\":{body}}}";
    }

    public static string SystemValue(string body) =>
        body.StartsWith("{\"\\u0000\":", StringComparison.Ordinal) ? body["{\"\\u0000\":".Length..^1] : body;
}

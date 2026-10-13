using System.Buffers.Text;
using System.Text.Json;
using Turso.Data.Sqlite;
using TerraBase.Engine.Lattices;
using TerraBase.Engine.Sqlite;

namespace TerraBase.Testing;

/// <summary>
/// Documents a test writes into a database file directly (docs/terrabase/multi-region-replication.md sections 5 and 15):
/// the materialized document row and the <c>_tb_state</c> row of one PUT, so the engine can write them again.
/// </summary>
public static class StoredStates
{
    /// <summary>The stamp of every write this helper makes, and so the version of its rows: 1 ms after the Unix epoch.</summary>
    public const long Version = 1 << 16;

    /// <summary>The node of every write this helper makes; no node draws region 0xFFFE.</summary>
    public static ReplicaId Replica { get; } = ReplicaId.Of(ushort.MaxValue - 1, 1, 0);

    /// <summary>
    /// Writes the document row and state of one PUT of <paramref name="body"/> (a JSON object) at <paramref name="version"/>
    /// (<see cref="Version"/> by default) into the existing table of its collection, or, with <paramref name="system"/>, into
    /// the system database's documents table (where <paramref name="body"/> may also be a scalar).
    /// </summary>
    public static async Task WriteAsync(
        SqliteConnection connection, string collection, string id, string body, long version = Version, SqliteTransaction? transaction = null,
        bool system = false)
    {
        ArgumentNullException.ThrowIfNull(connection);
        using var json = JsonDocument.Parse(system ? SystemBody(body) : body, DocumentState.JsonOptions);
        var state = DocumentState.Bottom.Put(new EntryVersion(new Hlc((ulong)version), Replica), json.RootElement);
        var digest = DocumentStateEncoding.Digest(collection, id, state);
        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = (system
            ? """
                DELETE FROM documents WHERE collection = $collection AND id = $id;
                INSERT INTO documents (collection, id, body) VALUES ($collection, $id, $body);
                """
            : $"""
                DELETE FROM {SqliteStorageTransaction.Table(collection)} WHERE id = $id;
                INSERT INTO {SqliteStorageTransaction.Table(collection)} (id, doc) VALUES ($id, $body);
                """) + $"""
            DELETE FROM _tb_state WHERE collection = $collection AND id = $id;
            INSERT INTO _tb_state (collection, id, bucket, state, stamp, origin, live, h1, h2)
            VALUES ($collection, $id, $bucket, $state, {version}, $origin, 1, $h1, $h2);
            """;
        insert.Parameters.AddWithValue("$collection", collection);
        insert.Parameters.AddWithValue("$id", id);
        insert.Parameters.AddWithValue("$body", System.Text.Encoding.UTF8.GetString(state.Materialize()!));
        insert.Parameters.AddWithValue("$bucket", SqliteStorageTransaction.Bucket(collection, id));
        insert.Parameters.AddWithValue("$state", Base64Url.EncodeToString(DocumentStateEncoding.EncodeStored(state)));
        insert.Parameters.AddWithValue("$origin", unchecked((long)Replica.Value));
        insert.Parameters.AddWithValue("$h1", unchecked((long)(ulong)(digest >> 64)));
        insert.Parameters.AddWithValue("$h2", unchecked((long)(ulong)digest));
        await insert.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// What the system database stores for <paramref name="body"/>: an object as it is, a scalar as the object
    /// <c>{"\u0000": value}</c>, which the engine reads back as the scalar.
    /// </summary>
    public static string SystemBody(string body)
    {
        using var json = JsonDocument.Parse(body, DocumentState.JsonOptions);
        return json.RootElement.ValueKind == JsonValueKind.Object ? body : $"{{\"\\u0000\":{body}}}";
    }

    /// <summary>A system database row's body as the engine reads it: a scalar without its wrapper (see <see cref="SystemBody"/>).</summary>
    public static string SystemValue(string body) =>
        body.StartsWith("{\"\\u0000\":", StringComparison.Ordinal) ? body["{\"\\u0000\":".Length..^1] : body;
}

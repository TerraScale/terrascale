using System.Buffers.Text;
using System.Text.Json;
using Turso.Data.Sqlite;
using TerraBase.Engine.Lattices;
using TerraBase.Engine.Sqlite;

namespace TerraBase.Testing;

/// <summary>
/// Documents a test writes into a client database file directly (docs/terrabase/multi-region-replication.md section 5):
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
    /// (<see cref="Version"/> by default) into the existing table of its collection.
    /// </summary>
    public static async Task WriteAsync(
        SqliteConnection connection, string collection, string id, string body, long version = Version, SqliteTransaction? transaction = null)
    {
        ArgumentNullException.ThrowIfNull(connection);
        using var json = JsonDocument.Parse(body, DocumentState.JsonOptions);
        var state = DocumentState.Bottom.Put(new EntryVersion(new Hlc((ulong)version), Replica), json.RootElement);
        var digest = DocumentStateEncoding.Digest(state);
        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = $"""
            DELETE FROM {SqliteStorageTransaction.Table(collection)} WHERE id = $id;
            INSERT INTO {SqliteStorageTransaction.Table(collection)} (id, doc) VALUES ($id, $body);
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
}

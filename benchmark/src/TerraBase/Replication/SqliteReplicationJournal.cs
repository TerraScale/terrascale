using Microsoft.Data.Sqlite;
using TerraBase.Storage.Schema;

namespace TerraBase.Replication;

/// <summary>Reads the durable publication journal without materializing a database snapshot.</summary>
public static class SqliteReplicationJournal
{
    /// <summary>
    /// Streams committed publication rows after <paramref name="fromSequence"/>
    /// in commit order. Body bytes are passed through unchanged.
    /// </summary>
    public static async IAsyncEnumerable<ReplicationRecord> ReadAsync(
        string databasePath,
        string origin,
        ulong fromSequence = 0,
        int batchSize = 256,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(origin);
        if (batchSize is < 1 or > 4096) throw new ArgumentOutOfRangeException(nameof(batchSize));
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.GetFullPath(databasePath), Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private, DefaultTimeout = 60
        }.ToString());
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        SqliteSchema.ConfigureReadOnlyConnection(connection);
        var cursor = checked((long)Math.Min(fromSequence, long.MaxValue));
        while (true)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT sequence, operation, tenant, collection, document_id, version, body, updated_at_ms
                FROM publication_journal
                WHERE sequence > $cursor ORDER BY sequence LIMIT $limit;
                """;
            command.Parameters.AddWithValue("$cursor", cursor);
            command.Parameters.AddWithValue("$limit", batchSize);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            var count = 0;
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var sequence = reader.GetInt64(0);
                var operation = reader.GetString(1);
                var key = new DocumentKey(new TenantId(reader.GetString(2)), new CollectionId(reader.GetString(3)), reader.GetString(4));
                var deleted = string.Equals(operation, "delete", StringComparison.OrdinalIgnoreCase);
                ReadOnlyMemory<byte>? bytes = deleted || reader.IsDBNull(6) ? null : (byte[])reader.GetValue(6);
                yield return new ReplicationRecord(key, new ReplicationDot(origin, checked((ulong)sequence)),
                    new Dictionary<string, ulong>(StringComparer.Ordinal), reader.GetInt64(7), deleted, null)
                { CanonicalBody = bytes };
                cursor = sequence;
                count++;
            }
            if (count < batchSize) yield break;
        }
    }
}

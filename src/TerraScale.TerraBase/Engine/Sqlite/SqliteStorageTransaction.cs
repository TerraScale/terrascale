using System.Text;
using Microsoft.Data.Sqlite;

namespace TerraScale.TerraBase.Engine.Sqlite;

internal sealed class SqliteStorageTransaction(SqliteConnection connection, SqliteTransaction transaction) : IStorageTransaction
{
    public async ValueTask<StoredDocument?> GetAsync(string collection, string id, CancellationToken cancellationToken)
    {
        var command = Command("SELECT id, version, body FROM documents WHERE collection = $collection AND id = $id;", collection);
        await using (command.ConfigureAwait(false))
        {
            command.Parameters.AddWithValue("$id", id);
            var documents = await ReadAsync(command, cancellationToken).ConfigureAwait(false);
            return documents.Count == 0 ? null : documents[0];
        }
    }

    public async ValueTask<long> PutAsync(string collection, string id, ReadOnlyMemory<byte> json, CancellationToken cancellationToken)
    {
        var command = Command("""
            INSERT INTO documents (collection, id, version, body) VALUES ($collection, $id, 1, $body)
            ON CONFLICT (collection, id) DO UPDATE SET version = version + 1, body = excluded.body
            RETURNING version;
            """, collection);
        await using (command.ConfigureAwait(false))
        {
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$body", Encoding.UTF8.GetString(json.Span));
            return (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
        }
    }

    public async ValueTask<bool> DeleteAsync(string collection, string id, CancellationToken cancellationToken)
    {
        var command = Command("DELETE FROM documents WHERE collection = $collection AND id = $id;", collection);
        await using (command.ConfigureAwait(false))
        {
            command.Parameters.AddWithValue("$id", id);
            return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
        }
    }

    public async ValueTask<DocumentPage> ListAsync(
        string collection, string? afterId, int limit, long maxBytes, CancellationToken cancellationToken)
    {
        var command = Command("""
            SELECT id, version, body, length(CAST(body AS BLOB)) FROM documents
            WHERE collection = $collection AND ($after IS NULL OR id > $after)
            ORDER BY id LIMIT $limit;
            """, collection);
        await using (command.ConfigureAwait(false))
        {
            // A missing cursor is NULL, not "", so the first page also holds an empty id.
            command.Parameters.AddWithValue("$after", (object?)afterId ?? DBNull.Value);
            // One extra row tells whether another page exists without reading its body.
            command.Parameters.AddWithValue("$limit", limit + 1);
            var documents = new List<StoredDocument>();
            long bytes = 0;
            var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await using (reader.ConfigureAwait(false))
            {
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    var size = reader.GetInt64(3);
                    if (documents.Count == limit || (documents.Count > 0 && bytes + size > maxBytes))
                    {
                        return new DocumentPage(documents, HasMore: true);
                    }

                    bytes += size;
                    documents.Add(new StoredDocument(reader.GetString(0), reader.GetInt64(1), Encoding.UTF8.GetBytes(reader.GetString(2))));
                }
            }

            return new DocumentPage(documents, HasMore: false);
        }
    }

    public async ValueTask CommitAsync(CancellationToken cancellationToken) =>
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

    public async ValueTask DisposeAsync()
    {
        // Disposing an uncommitted transaction rolls it back; the connection is released even if that fails.
        try
        {
            await transaction.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }
    }

    private SqliteCommand Command(string sql, string collection)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Parameters.AddWithValue("$collection", collection);
        return command;
    }

    private static async Task<List<StoredDocument>> ReadAsync(SqliteCommand command, CancellationToken cancellationToken)
    {
        var documents = new List<StoredDocument>();
        var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await using (reader.ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                documents.Add(new StoredDocument(reader.GetString(0), reader.GetInt64(1), Encoding.UTF8.GetBytes(reader.GetString(2))));
            }
        }

        return documents;
    }
}

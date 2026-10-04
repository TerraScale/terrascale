using System.Text;
using Microsoft.Data.Sqlite;

namespace TerraScale.TerraBase.Engine.Sqlite;

internal sealed class SqliteStorageTransaction(SqliteDatabaseStore store, SqliteSession session, SqliteTransaction transaction)
    : IStorageTransaction
{
    private int _disposed;

    // Put is prepared once per transaction, not cached on the session: a prepared statement keeps a
    // copy of the last bound body (up to the document size limit), which would stay in memory for as
    // long as the session is idle. Disposing it when the transaction ends releases that copy.
    private SqliteCommand? _put;

    public async ValueTask<StoredDocument?> GetAsync(string collection, string id, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var command = Command(SqliteStatements.Get, collection);
        command.Parameters.AddWithValue("$id", id);
        var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await using (reader.ConfigureAwait(false))
        {
            return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
                // The body is TEXT; reading it as bytes returns its UTF-8 without decoding it.
                ? new StoredDocument(id, reader.GetInt64(0), reader.GetFieldValue<byte[]>(1))
                : null;
        }
    }

    public async ValueTask<long> PutAsync(string collection, string id, ReadOnlyMemory<byte> json, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (_put is null)
        {
            _put = session.Connection.CreateCommand();
            _put.Transaction = transaction;
            _put.CommandText = SqliteStatements.Put;
        }

        _put.Parameters.Clear();
        _put.Parameters.AddWithValue("$collection", collection);
        _put.Parameters.AddWithValue("$id", id);
        _put.Parameters.AddWithValue("$body", Encoding.UTF8.GetString(json.Span));
        return (long)(await _put.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }

    public async ValueTask<bool> DeleteAsync(string collection, string id, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var command = Command(SqliteStatements.Delete, collection);
        command.Parameters.AddWithValue("$id", id);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
    }

    public async ValueTask<DocumentPage> ListAsync(
        string collection, string? afterId, int limit, long maxBytes, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        // A missing cursor is not "", so the first page also holds an empty id.
        var command = Command(afterId is null ? SqliteStatements.ListFirst : SqliteStatements.ListAfter, collection);
        if (afterId is not null)
        {
            command.Parameters.AddWithValue("$after", afterId);
        }

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
                documents.Add(new StoredDocument(reader.GetString(0), reader.GetInt64(1), reader.GetFieldValue<byte[]>(2)));
            }
        }

        return new DocumentPage(documents, HasMore: false);
    }

    public async ValueTask CommitAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        // The session goes back to the pool exactly once, even if this is disposed again after
        // another transaction rented it.
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        // Disposing an uncommitted transaction rolls it back. A session whose rollback failed is
        // closed instead of reused.
        try
        {
            if (_put is not null)
            {
                await _put.DisposeAsync().ConfigureAwait(false);
            }

            await transaction.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
            session.Dispose();
            throw;
        }

        store.Return(session);
    }

    // After disposal the session may already belong to another transaction: refuse before touching it.
    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    private SqliteCommand Command(string sql, string collection)
    {
        var command = session.Command(sql, transaction);
        command.Parameters.AddWithValue("$collection", collection);
        return command;
    }
}

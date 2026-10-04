using System.Diagnostics;
using System.Text;
using Microsoft.Data.Sqlite;
using SQLitePCL;

namespace TerraScale.TerraBase.Engine.Sqlite;

internal sealed class SqliteStorageTransaction(SqliteDatabaseStore store, SqliteSession session, SqliteTransaction transaction)
    : IStorageTransaction
{
    public Guid Incarnation => store.Incarnation;

    private int _disposed;

    // Put is prepared once per transaction, not cached on the session: a prepared statement keeps a
    // copy of the last bound body (up to the document size limit), which would stay in memory for as
    // long as the session is idle. Disposing it when the transaction ends releases that copy.
    private SqliteCommand? _put;

    // This transaction's commit offset, allocated by its first document write.
    private long? _offset;

    public async ValueTask<StoredDocument?> GetAsync(string collection, string id, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var command = Command(SqliteStatements.Get, collection);
        command.Parameters.AddWithValue("$id", id);
        var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await using (reader.ConfigureAwait(false))
        {
            return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) && !reader.IsDBNull(1)
                // The body is TEXT; reading it as bytes returns its UTF-8 without decoding it.
                ? new StoredDocument(id, reader.GetInt64(0), reader.GetFieldValue<byte[]>(1), Incarnation)
                : null;
        }
    }

    public async ValueTask<DocumentVersion?> GetVersionAsync(string collection, string id, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var command = Command(SqliteStatements.Version, collection);
        command.Parameters.AddWithValue("$id", id);
        var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await using (reader.ConfigureAwait(false))
        {
            return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
                ? new DocumentVersion(reader.GetInt64(0), reader.GetBoolean(1))
                : null;
        }
    }

    public async ValueTask<long> PutAsync(string collection, string id, ReadOnlyMemory<byte> json, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var offset = await OffsetAsync(cancellationToken).ConfigureAwait(false);
        if (_put is null)
        {
            _put = session.Connection.CreateCommand();
            _put.Transaction = transaction;
            _put.CommandText = SqliteStatements.Put;
        }

        _put.Parameters.Clear();
        _put.Parameters.AddWithValue("$collection", collection);
        _put.Parameters.AddWithValue("$id", id);
        _put.Parameters.AddWithValue("$version", offset);
        _put.Parameters.AddWithValue("$body", Encoding.UTF8.GetString(json.Span));
        await _put.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return offset;
    }

    public async ValueTask<bool> DeleteAsync(string collection, string id, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var command = Command(SqliteStatements.Delete, collection);
        command.Parameters.AddWithValue("$id", id);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
    }

    public async ValueTask<long?> TombstoneAsync(string collection, string id, CancellationToken cancellationToken)
    {
        // Liveness from the version metadata only; the body is never read.
        if (await GetVersionAsync(collection, id, cancellationToken).ConfigureAwait(false) is not { Deleted: false })
        {
            return null;
        }

        // ponytail: tombstones are kept forever (they carry the version a client recreates with);
        // reap them once replication defines when a tombstone is no longer needed.
        var offset = await OffsetAsync(cancellationToken).ConfigureAwait(false);
        var command = Command(SqliteStatements.Tombstone, collection);
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$version", offset);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return offset;
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
                documents.Add(new StoredDocument(reader.GetString(0), reader.GetInt64(1), reader.GetFieldValue<byte[]>(2), Incarnation));
            }
        }

        return new DocumentPage(documents, HasMore: false);
    }

    public async ValueTask<QueryPage> QueryAsync(DocumentQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ThrowIfDisposed();
        return await WithQueryBudgetAsync(async () =>
        {
            if (query.Sort.Count > 0 && !query.Sort[0].Path.IsId)
            {
                await CheckSortBudgetAsync(query, cancellationToken).ConfigureAwait(false);
            }

            return await ReadQueryAsync(query, cancellationToken).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<T> WithQueryBudgetAsync<T>(Func<ValueTask<T>> execute, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        var callbacks = 0;
        // A lock timeout does not bound query work. Interrupt after 5 million VM instructions or 5 seconds,
        // including cancellation, then remove the handler before this session can be reused.
        raw.sqlite3_progress_handler(session.Connection.Handle, 1000,
            _ => cancellationToken.IsCancellationRequested || ++callbacks >= 5000
                || Stopwatch.GetElapsedTime(started) >= TimeSpan.FromSeconds(5) ? 1 : 0, null);
        try
        {
            return await execute().ConfigureAwait(false);
        }
        catch (SqliteException error) when (error.SqliteErrorCode == raw.SQLITE_INTERRUPT)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new QueryCapacityException("query execution budget exceeded", error);
        }
        finally
        {
            raw.sqlite3_progress_handler(session.Connection.Handle, 0, null, null);
        }
    }

    // Sort only after bounding both candidate count and JSON bytes, so SQLite cannot retain an unbounded
    // collection's bodies in its temporary sorter. octet_length reads size metadata without loading the body.
    private async ValueTask CheckSortBudgetAsync(DocumentQuery query, CancellationToken cancellationToken)
    {
        var command = Uncached(query.Collection);
        await using (command.ConfigureAwait(false))
        {
            command.CommandText = """
                SELECT octet_length(body) FROM documents INDEXED BY documents_live
                WHERE collection = $collection AND body IS NOT NULL ORDER BY id LIMIT 10001;
                """;
            var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await using (reader.ConfigureAwait(false))
            {
                long bytes = 0;
                var rows = 0;
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    bytes += reader.GetInt64(0);
                    if (++rows > 10000 || bytes > query.MaxBytes)
                    {
                        throw new QueryCapacityException("query sort budget exceeded") { SortBudget = true };
                    }
                }
            }
        }
    }

    private async ValueTask<QueryPage> ReadQueryAsync(DocumentQuery query, CancellationToken cancellationToken)
    {
        // Compiled per request, so not cached on the session (the cache would grow with every filter shape).
        var command = Uncached(query.Collection);
        await using (command.ConfigureAwait(false))
        {
            command.CommandText = new SqliteQueryCompiler(command.Parameters).Compile(query);
            var rows = new List<QueryRow>();
            long bytes = 0;
            var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await using (reader.ConfigureAwait(false))
            {
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    var size = reader.GetInt64(3);
                    if (rows.Count == query.Limit || (rows.Count > 0 && bytes + size > query.MaxBytes))
                    {
                        return new QueryPage(rows, HasMore: true);
                    }

                    bytes += size;
                    var keys = new object?[reader.FieldCount - 4];
                    for (var i = 0; i < keys.Length; i++)
                    {
                        // The provider's text conversion stops at NUL. Read string key bytes with their
                        // SQLite length so a continuation carries the complete sort value.
                        keys[i] = reader.IsDBNull(4 + i) ? null
                            : i % 2 == 1 && reader.GetInt64(3 + i) == 5
                                ? Encoding.UTF8.GetString(reader.GetFieldValue<byte[]>(4 + i))
                                : reader.GetValue(4 + i);
                    }

                    rows.Add(new QueryRow(new StoredDocument(reader.GetString(0), reader.GetInt64(1), reader.GetFieldValue<byte[]>(2), Incarnation), keys));
                }
            }

            return new QueryPage(rows, HasMore: false);
        }
    }

    public async ValueTask<(long Count, bool Complete)> CountAsync(
        string collection, DocumentFilter? filter, long maxExamined, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        return await WithQueryBudgetAsync(() => ReadCountAsync(collection, filter, maxExamined, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<(long Count, bool Complete)> ReadCountAsync(
        string collection, DocumentFilter? filter, long maxExamined, CancellationToken cancellationToken)
    {
        var command = Uncached(collection);
        await using (command.ConfigureAwait(false))
        {
            command.CommandText = new SqliteQueryCompiler(command.Parameters).CompileCount(filter, maxExamined);
            var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await using (reader.ConfigureAwait(false))
            {
                await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                return (reader.GetInt64(1), reader.GetInt64(0) <= maxExamined);
            }
        }
    }

    public async ValueTask<bool> CreateCollectionAsync(CollectionInfo collection, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(collection);
        ThrowIfDisposed();
        var command = Command(SqliteStatements.CreateCollection, collection.Name);
        command.Parameters.AddWithValue("$created", collection.CreatedAt);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
    }

    public async ValueTask<CollectionInfo?> GetCollectionAsync(string name, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var reader = await Command(SqliteStatements.GetCollection, name).ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await using (reader.ConfigureAwait(false))
        {
            return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
                ? new CollectionInfo(reader.GetString(0), reader.GetInt64(1))
                : null;
        }
    }

    public async ValueTask<(IReadOnlyList<CollectionInfo> Collections, bool HasMore)> ListCollectionsAsync(
        string? afterName, int limit, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var command = session.Command(afterName is null ? SqliteStatements.ListCollectionsFirst : SqliteStatements.ListCollectionsAfter, transaction);
        if (afterName is not null)
        {
            command.Parameters.AddWithValue("$after", afterName);
        }

        // One extra row tells whether another page exists.
        command.Parameters.AddWithValue("$limit", limit + 1);
        var collections = new List<CollectionInfo>();
        var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await using (reader.ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (collections.Count == limit)
                {
                    return (collections, true);
                }

                collections.Add(new CollectionInfo(reader.GetString(0), reader.GetInt64(1)));
            }
        }

        return (collections, false);
    }

    public async ValueTask<bool> DropCollectionAsync(string name, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        // ponytail: one transaction deletes the whole collection; batch it if very large drops stall writers.
        await Command(SqliteStatements.DropDocuments, name).ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return await Command(SqliteStatements.DropCollection, name).ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
    }

    public async ValueTask<long> CommitAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var offset = _offset
            ?? (long?)await session.Command(SqliteStatements.CurrentOffset, transaction).ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)
            ?? 0;
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return offset;
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

    // Every write of one transaction shares one offset, allocated under the database write lock.
    private async ValueTask<long> OffsetAsync(CancellationToken cancellationToken)
    {
        _offset ??= await session.Command(SqliteStatements.NextOffset, transaction).ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is long offset
            ? offset
            : throw new OverflowException("the commit offset is exhausted");
        return _offset.Value;
    }

    // After disposal the session may already belong to another transaction: refuse before touching it.
    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    private SqliteCommand Command(string sql, string collection)
    {
        var command = session.Command(sql, transaction);
        command.Parameters.AddWithValue("$collection", collection);
        return command;
    }

    private SqliteCommand Uncached(string collection)
    {
        var command = session.Connection.CreateCommand();
        command.Transaction = transaction;
        command.Parameters.AddWithValue("$collection", collection);
        return command;
    }
}

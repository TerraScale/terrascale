using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using SQLitePCL;

namespace TerraScale.TerraBase.Engine.Sqlite;

// committed runs after a write transaction that allocated an offset commits; null for reads.
internal sealed class SqliteStorageTransaction(
    SqliteDatabaseStore store, SqliteSession session, SqliteTransaction transaction, bool write, Action? committed)
    : IStorageTransaction
{
    // One commit must fit one shape log chunk (8 MiB body): at most 1024 writes, 1 MiB documents,
    // 1024-byte ids and 7 MiB including the framing of every op, leaving 1 MiB for the chunk around it.
    private const long MaxWrittenBytes = 7 * 1024 * 1024;
    private const int MaxDocumentBytes = 1024 * 1024;
    private const int MaxWrites = 1024;
    private const int MaxIdBytes = 1024;

    // An upsert op's framing beyond its document and escaped id: keys, version hash, separators.
    private const int OpFramingBytes = 128;

    // Lossy decoding would replace each invalid byte with a 3-byte U+FFFD after the size was admitted.
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public Guid Incarnation => store.Incarnation;

    private int _disposed;

    // Set by a rejected or failed write: the transaction may then only roll back.
    private bool _writeFailed;
    private int _writes;
    private long _writtenBytes;

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
        RequireWritable();
        Reserve(id, json.Length);
        return await WriteAsync(async offset =>
        {
            if (_put is null)
            {
                _put = session.Connection.CreateCommand();
                _put.Transaction = transaction;
                _put.CommandText = SqliteStatements.Put;
            }

            var body = StrictUtf8.GetString(json.Span);
            _put.Parameters.Clear();
            _put.Parameters.AddWithValue("$collection", collection);
            _put.Parameters.AddWithValue("$id", id);
            _put.Parameters.AddWithValue("$version", offset);
            _put.Parameters.AddWithValue("$body", body);
            await _put.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await AppendChangeAsync(collection, id, offset, body, cancellationToken).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<bool> DeleteAsync(string collection, string id, CancellationToken cancellationToken)
    {
        RequireWritable();
        var command = Command(SqliteStatements.Delete, collection);
        command.Parameters.AddWithValue("$id", id);
        try
        {
            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 0)
            {
                return false;
            }
        }
        catch
        {
            // The row may already be gone (a failing AFTER DELETE trigger): never commit that without its history.
            _writeFailed = true;
            throw;
        }

        // Gone from the documents, but shape history still records that the document left.
        if (Shaped)
        {
            Reserve(id, 0);
            await WriteAsync(offset => AppendChangeAsync(collection, id, offset, null, cancellationToken), cancellationToken).ConfigureAwait(false);
        }

        return true;
    }

    public async ValueTask<long?> TombstoneAsync(string collection, string id, CancellationToken cancellationToken)
    {
        RequireWritable();
        // Liveness from the version metadata only; the body is never read.
        if (await GetVersionAsync(collection, id, cancellationToken).ConfigureAwait(false) is not { Deleted: false })
        {
            return null;
        }

        Reserve(id, 0);
        // ponytail: tombstones are kept forever (they carry the version a client recreates with);
        // reap them once replication defines when a tombstone is no longer needed.
        return await WriteAsync(async offset =>
        {
            var command = Command(SqliteStatements.Tombstone, collection);
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$version", offset);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await AppendChangeAsync(collection, id, offset, null, cancellationToken).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    // Shape history and the per-commit limits apply to client databases; clients cannot name _system.
    private bool Shaped => !store.IsSystem;

    // Runs a write at this transaction's offset; any failure leaves the transaction able only to roll back.
    private async ValueTask<long> WriteAsync(Func<long, Task> apply, CancellationToken cancellationToken)
    {
        try
        {
            var offset = await OffsetAsync(cancellationToken).ConfigureAwait(false);
            await apply(offset).ConfigureAwait(false);
            return offset;
        }
        catch
        {
            _writeFailed = true;
            throw;
        }
    }

    private async Task AppendChangeAsync(string collection, string id, long offset, string? body, CancellationToken cancellationToken)
    {
        if (!Shaped)
        {
            return;
        }

        if (await AppendAsync().ConfigureAwait(false) == 1)
        {
            return;
        }

        // A writer below the Documents API (the engine's own callers) may skip creating the collection: create its
        // metadata, a fresh generation, so its history is never dropped, then append.
        var create = Command(SqliteStatements.CreateCollection, collection);
        create.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        await create.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if (await AppendAsync().ConfigureAwait(false) != 1)
        {
            throw new InvalidOperationException("a document write needs its collection's metadata for shape history");
        }

        async Task<int> AppendAsync()
        {
            var command = Command(SqliteStatements.AppendChange, collection);
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$version", offset);
            command.Parameters.AddWithValue("$body", (object?)body ?? DBNull.Value);
            return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    // Admits one write against the per-commit limits of a client database.
    private void Reserve(string id, int documentBytes)
    {
        if (!Shaped)
        {
            return;
        }

        var bytes = documentBytes + (long)JsonEncodedText.Encode(id).EncodedUtf8Bytes.Length + OpFramingBytes;
        // An empty id has no successor to resume after; no writer ever produced one (schema 1 required 1 to 256 characters).
        if (id.Length == 0 || documentBytes > MaxDocumentBytes || Encoding.UTF8.GetByteCount(id) > MaxIdBytes || _writes == MaxWrites
            || _writtenBytes + bytes > MaxWrittenBytes)
        {
            _writeFailed = true;
            throw new StorageWriteLimitException(
                "a transaction writes at most 1024 documents of at most 1 MiB, 7 MiB in total with framing, with ids of 1 to 1024 bytes");
        }

        _writes++;
        _writtenBytes += bytes;
    }

    private void RequireWritable()
    {
        ThrowIfDisposed();
        if (!write || _writeFailed)
        {
            // A refused write also refuses the commit, so a caller cannot commit around it.
            _writeFailed = true;
            throw new InvalidOperationException("writes need a write transaction without a failed write");
        }
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
        StartQueryBudget(cancellationToken);
        try
        {
            return await execute().ConfigureAwait(false);
        }
        catch (SqliteException error) when (error.SqliteErrorCode == raw.SQLITE_INTERRUPT)
        {
            throw Exhausted(error, cancellationToken);
        }
        finally
        {
            StopQueryBudget();
        }
    }

    // A lock timeout does not bound query work. Interrupt after 5 million VM instructions or 5 seconds,
    // including cancellation; StopQueryBudget removes the handler before this session can be reused.
    // A streaming reader restarts the clock before each step (QueryBudget.Restart), so the deadline
    // measures SQLite's work, not the consumer's time between rows; the instruction count accumulates.
    private QueryBudget StartQueryBudget(CancellationToken cancellationToken)
    {
        var budget = new QueryBudget();
        raw.sqlite3_progress_handler(session.Connection.Handle, 1000,
            _ => cancellationToken.IsCancellationRequested || ++budget.Callbacks >= 5000 || budget.Spent >= QueryDeadline ? 1 : 0, null);
        return budget;
    }

    private static readonly TimeSpan QueryDeadline = TimeSpan.FromSeconds(5);

    // Ids one snapshot page examines at most: about two index seeks each.
    private const int MaxSnapshotSteps = 10_000;

    // Ids first written after the offset one snapshot page passes over at most (one births entry each). Beyond,
    // the snapshot is too old to read within bounded work: the page expires instead of shifting its boundary.
    private const int MaxSnapshotSkips = 50_000;

    // Time spent in SQLite across a read's steps: a streaming reader pauses it while its consumer works, so the
    // deadline bounds the read's total execution, not each step and not the consumer.
    private sealed class QueryBudget
    {
        // Running since this timestamp, or 0 while paused; Spent adds the time before the last pause.
        private long _running = Stopwatch.GetTimestamp();
        private TimeSpan _paused;

        public int Callbacks { get; set; }

        public TimeSpan Spent => _running == 0 ? _paused : _paused + Stopwatch.GetElapsedTime(_running);

        public void Resume()
        {
            if (_running == 0)
            {
                _running = Stopwatch.GetTimestamp();
            }
        }

        public void Pause()
        {
            if (_running != 0)
            {
                _paused += Stopwatch.GetElapsedTime(_running);
                _running = 0;
            }
        }
    }

    private void StopQueryBudget() => raw.sqlite3_progress_handler(session.Connection.Handle, 0, null, null);

    private static QueryCapacityException Exhausted(SqliteException error, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return new QueryCapacityException("query execution budget exceeded", error);
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

    public async ValueTask<SnapshotPage> SnapshotAsync(DocumentQuery query, long offset, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ThrowIfDisposed();
        if (await GetCollectionAsync(query.Collection, cancellationToken).ConfigureAwait(false) is not { } collection)
        {
            return new SnapshotPage([], HasMore: false, ResumeAfter: null);
        }

        var command = Uncached(query.Collection);
        await using (command.ConfigureAwait(false))
        {
            // One streamed read of the births key in id order: each id that existed at the offset is a step with
            // its last write at the offset, so the cost follows the ids read, never the depth of their history.
            command.CommandText = new SqliteQueryCompiler(command.Parameters).CompileSnapshot(query.Filter, collection.Generation, offset);
            command.Parameters.AddWithValue("$after", query.After?.Id ?? "");
            var rows = new List<QueryRow>();
            long bytes = 0;
            // A row may run fewer instructions than the progress handler's interval, so the page also counts
            // its rows and checks the deadline between them.
            var budget = StartQueryBudget(cancellationToken);
            try
            {
                var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                await using (reader.ConfigureAwait(false))
                {
                    // Steps count only ids that existed at the offset, so a replay examines the same ids and ends at
                    // the same boundary; ids first written later are skips, bounded separately.
                    string? examined = null;
                    for (int steps = 0, skips = 0; ;)
                    {
                        // A page at its step, row or byte limit ends before reading on: later ids past its boundary
                        // are the next page's to pass over, so they can neither expire nor delay a complete page.
                        if (steps == MaxSnapshotSteps)
                        {
                            return new SnapshotPage(rows, HasMore: true, ResumeAfter: examined);
                        }

                        if (rows.Count == query.Limit)
                        {
                            return new SnapshotPage(rows, HasMore: true, ResumeAfter: rows[^1].Document.Id);
                        }

                        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                        {
                            return new SnapshotPage(rows, HasMore: false, ResumeAfter: null);
                        }

                        if (budget.Spent >= QueryDeadline)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            throw new QueryCapacityException("query execution budget exceeded");
                        }

                        if (!reader.GetBoolean(1))
                        {
                            // Too many later ids to pass over within bounded work: the snapshot is too old to read.
                            // Answering with other boundaries instead would depend on later writes.
                            if (++skips > MaxSnapshotSkips)
                            {
                                return new SnapshotPage([], HasMore: false, ResumeAfter: null, Expired: true);
                            }

                            continue;
                        }

                        steps++;
                        var id = reader.GetString(0);
                        // A tombstone at the offset, or rejected by the filter.
                        if (reader.IsDBNull(3))
                        {
                            examined = id;
                            continue;
                        }

                        bytes += reader.GetInt64(4);
                        rows.Add(new QueryRow(new StoredDocument(id, reader.GetInt64(2), reader.GetFieldValue<byte[]>(3), Incarnation), []));
                        examined = id;
                        // The last row may cross the byte limit; the caller trims the page to its serialized size.
                        if (bytes >= query.MaxBytes)
                        {
                            return new SnapshotPage(rows, HasMore: true, ResumeAfter: id);
                        }
                    }
                }
            }
            catch (SqliteException error) when (error.SqliteErrorCode == raw.SQLITE_INTERRUPT)
            {
                throw Exhausted(error, cancellationToken);
            }
            finally
            {
                StopQueryBudget();
            }
        }
    }

    public async ValueTask<long> GetOffsetAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        return (long?)await session.Command(SqliteStatements.CurrentOffset, transaction).ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? 0;
    }

    public async ValueTask<long> GetHistoryFloorAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        return (long)(await session.Command(SqliteStatements.HistoryFloor, transaction).ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }

    public async IAsyncEnumerable<StoredDocument> ChangesAsync(
        string collection, DocumentFilter? filter, long after, long through, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var command = Uncached(collection);
        await using (command.ConfigureAwait(false))
        {
            command.CommandText = new SqliteQueryCompiler(command.Parameters).CompileChanges(filter, after, through);
            // The query budget covers the whole enumeration, and the handler goes even when the consumer stops early.
            var budget = StartQueryBudget(cancellationToken);
            try
            {
                SqliteDataReader reader;
                try
                {
                    reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (SqliteException error) when (error.SqliteErrorCode == raw.SQLITE_INTERRUPT)
                {
                    throw Exhausted(error, cancellationToken);
                }

                await using (reader.ConfigureAwait(false))
                {
                    while (true)
                    {
                        bool read;
                        try
                        {
                            budget.Resume();
                            read = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                            budget.Pause();
                        }
                        catch (SqliteException error) when (error.SqliteErrorCode == raw.SQLITE_INTERRUPT)
                        {
                            throw Exhausted(error, cancellationToken);
                        }

                        if (budget.Spent >= QueryDeadline)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            throw new QueryCapacityException("query execution budget exceeded");
                        }

                        if (!read)
                        {
                            yield break;
                        }

                        yield return reader.IsDBNull(2)
                            ? new StoredDocument(reader.GetString(0), reader.GetInt64(1), ReadOnlyMemory<byte>.Empty, Incarnation, Deleted: true)
                            : new StoredDocument(reader.GetString(0), reader.GetInt64(1), reader.GetFieldValue<byte[]>(2), Incarnation);
                    }
                }
            }
            finally
            {
                StopQueryBudget();
            }
        }
    }

    public async ValueTask<bool> CreateCollectionAsync(CollectionInfo collection, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(collection);
        RequireWritable();
        var command = Command(SqliteStatements.CreateCollection, collection.Name);
        command.Parameters.AddWithValue("$created", collection.CreatedAt);
        try
        {
            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 0)
            {
                return false;
            }
        }
        catch
        {
            _writeFailed = true;
            throw;
        }

        // A creation is a commit of its own offset, so log readers and live waits see it. If allocating
        // the offset fails, the transaction can no longer commit: the insert and the offset are one unit.
        await WriteAsync(_ => Task.CompletedTask, cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async ValueTask<CollectionInfo?> GetCollectionAsync(string name, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var reader = await Command(SqliteStatements.GetCollection, name).ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await using (reader.ConfigureAwait(false))
        {
            return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
                ? new CollectionInfo(reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2))
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

                collections.Add(new CollectionInfo(reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2)));
            }
        }

        return (collections, false);
    }

    public async ValueTask<bool> DropCollectionAsync(string name, CancellationToken cancellationToken)
    {
        RequireWritable();
        try
        {
            // ponytail: one transaction deletes the whole collection; batch it if very large drops stall writers.
            // History first: it finds the dropped generation through the collection row.
            await Command(SqliteStatements.DropHistory, name).ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await Command(SqliteStatements.DropDocuments, name).ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            if (await Command(SqliteStatements.DropCollection, name).ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 0)
            {
                return false;
            }
        }
        catch
        {
            _writeFailed = true;
            throw;
        }

        await WriteAsync(_ => Task.CompletedTask, cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async ValueTask<long> CommitAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (_writeFailed)
        {
            throw new InvalidOperationException("a transaction with a rejected or failed write cannot commit");
        }

        var offset = _offset
            ?? (long?)await session.Command(SqliteStatements.CurrentOffset, transaction).ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)
            ?? 0;
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        // Only a commit that allocated an offset changed anything a shape reader or live wait can see.
        if (_offset is not null)
        {
            committed?.Invoke();
        }

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
            store.Discard(session);
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

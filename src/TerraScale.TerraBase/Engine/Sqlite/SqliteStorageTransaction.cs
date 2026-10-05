using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using SQLitePCL;
using TerraScale.TerraBase.Primitives;

namespace TerraScale.TerraBase.Engine.Sqlite;

/// <param name="store">The database this transaction belongs to.</param>
/// <param name="session">The rented session; returned to <paramref name="store"/> on dispose.</param>
/// <param name="transaction">The SQLite transaction.</param>
/// <param name="write">Whether mutations are allowed.</param>
/// <param name="committed">Runs after a commit advances the local shape history or its head.</param>
/// <param name="changesCommitted">When set, local mutations are captured and it receives the group's sequence after commit.</param>
internal sealed partial class SqliteStorageTransaction(
    SqliteDatabaseStore store, SqliteSession session, SqliteTransaction transaction, bool write, Action? committed, Action<long>? changesCommitted)
    : IStorageTransaction
{
    // One commit must fit one shape log chunk (8 MiB body): at most 1024 writes, 1 MiB documents,
    // 1024-byte ids and 7 MiB including the framing of every op, leaving 1 MiB for the chunk around it.
    private const long MaxWrittenBytes = 7 * 1024 * 1024;
    private const int MaxDocumentBytes = DocumentJson.MaxBytes;
    private const int MaxWrites = 1024;
    private const int MaxIdBytes = 1024;

    // An upsert op's framing beyond its document and escaped id: keys, version hash, separators.
    private const int OpFramingBytes = 128;

    // Lossy decoding would replace each invalid byte with a 3-byte U+FFFD after the size was admitted.
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public Guid Incarnation => store.Incarnation;

    private readonly List<DocumentChange>? _changes = changesCommitted is null ? null : [];

    // Body statements live only for this transaction: idle sessions must not retain bound JSON.
    private readonly Dictionary<string, SqliteCommand> _bodyCommands = [with(StringComparer.Ordinal)];

    // Winning imports wait for commit so the whole group shares one local history offset.
    private readonly List<DocumentChange> _appliedChanges = [];
    private long? _importedFromOffset;
    private bool _metadataChanged;
    private int _importedCount;
    private long _importedBytes;
    private long _changeBytes;
    private int _disposed;
    private bool? _replicatedMutation;

    // Set by a rejected or failed write: the transaction may then only roll back.
    private bool _faulted;
    private int _writes;
    private long _writtenBytes;

    // This transaction's local commit offset, separate from incoming document merge versions.
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

    private string Admit(ReadOnlyMemory<byte> json)
    {
        try
        {
            // The private system catalog also stores scalar indexes; tenant documents must be objects.
            return DocumentJson.Read(json, requireObject: store.Tenant is not null);
        }
        catch
        {
            _faulted = true;
            throw;
        }
    }

    public async ValueTask<long> PutAsync(string collection, string id, ReadOnlyMemory<byte> json, CancellationToken cancellationToken)
    {
        ThrowIfReadOnly();
        DocumentMutation(replicated: false);
        var body = Admit(json);
        Reserve(collection, id, json.Length);
        return await WriteAsync(async offset =>
        {
            if (_changes is not null)
            {
                Capture(new DocumentChange(collection, id, offset, json));
            }

            var put = BodyCommand(SqliteStatements.Put, collection, id);
            put.Parameters.AddWithValue("$version", offset);
            put.Parameters.AddWithValue("$body", body);
            await put.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await AppendChangeAsync(collection, id, offset, offset, body, cancellationToken).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<bool> DeleteAsync(string collection, string id, CancellationToken cancellationToken)
    {
        ThrowIfReadOnly();
        DocumentMutation(replicated: false);
        try
        {
            var command = Command(SqliteStatements.Delete, collection);
            command.Parameters.AddWithValue("$id", id);
            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 0)
            {
                return false;
            }

            // Physical removal remains available internally; history and peers still need its deletion.
            if (Shaped || _changes is not null)
            {
                Reserve(collection, id, 0);
                await WriteAsync(async offset =>
                {
                    if (_changes is not null)
                    {
                        Capture(new DocumentChange(collection, id, offset, null));
                    }

                    await AppendChangeAsync(collection, id, offset, offset, null, cancellationToken).ConfigureAwait(false);
                }, cancellationToken).ConfigureAwait(false);
            }

            return true;
        }
        catch
        {
            // The row may already be gone (a failing AFTER DELETE trigger): never commit it without its history.
            _faulted = true;
            throw;
        }
    }

    public async ValueTask<long?> TombstoneAsync(string collection, string id, CancellationToken cancellationToken)
    {
        ThrowIfReadOnly();
        DocumentMutation(replicated: false);
        // Liveness from the version metadata only; the body is never read.
        if (await GetVersionAsync(collection, id, cancellationToken).ConfigureAwait(false) is not { Deleted: false })
        {
            return null;
        }

        Reserve(collection, id, 0);
        // ponytail: tombstones are kept forever (they carry the version a client recreates with);
        // reap them once replication defines when a tombstone is no longer needed.
        return await WriteAsync(async offset =>
        {
            if (_changes is not null)
            {
                Capture(new DocumentChange(collection, id, offset, null));
            }

            var command = Command(SqliteStatements.Tombstone, collection);
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$version", offset);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await AppendChangeAsync(collection, id, offset, offset, null, cancellationToken).ConfigureAwait(false);
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
            _faulted = true;
            throw;
        }
    }

    private async Task AppendChangeAsync(string collection, string id, long offset, long documentVersion, string? body, CancellationToken cancellationToken)
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
            command.Parameters.AddWithValue("$documentVersion", documentVersion);
            command.Parameters.AddWithValue("$body", (object?)body ?? DBNull.Value);
            return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    // Admits one write against the per-commit limits of a client database.
    private void Reserve(string collection, string id, int documentBytes)
    {
        if (!Shaped)
        {
            return;
        }

        if (_changes is not null && !DocumentChange.HasValidKey(collection, id))
        {
            _faulted = true;
            throw new InvalidDataException("a captured change has an invalid collection or id");
        }

        var bytes = documentBytes + (long)JsonEncodedText.Encode(id).EncodedUtf8Bytes.Length + OpFramingBytes;
        // An empty id has no successor to resume after; no writer ever produced one (schema 1 required 1 to 256 characters).
        if (id.Length == 0 || documentBytes > MaxDocumentBytes || Encoding.UTF8.GetByteCount(id) > MaxIdBytes || _writes == MaxWrites
            || _writtenBytes + bytes > MaxWrittenBytes)
        {
            _faulted = true;
            throw new StorageWriteLimitException(
                "a transaction writes at most 1024 documents of at most 1 MiB, 7 MiB in total with framing, with ids of 1 to 1024 bytes");
        }

        _writes++;
        _writtenBytes += bytes;
    }

    private void ThrowIfReadOnly()
    {
        ThrowIfDisposed();
        if (!write)
        {
            _faulted = true;
            throw new InvalidOperationException("a read transaction cannot mutate database state");
        }

        if (_faulted)
        {
            throw new InvalidOperationException("an operation of this transaction failed, so it can only be rolled back");
        }
    }

    public ValueTask<DocumentPage> ListAsync(
        string collection, string? afterId, int limit, long maxBytes, CancellationToken cancellationToken) =>
        WithQueryBudgetAsync(() => ReadListAsync(collection, afterId, limit, maxBytes, cancellationToken), cancellationToken);

    private async ValueTask<DocumentPage> ReadListAsync(
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
                await CheckSortBudgetAsync(query.Collection, query.MaxBytes, cancellationToken).ConfigureAwait(false);
            }

            return await ReadQueryAsync(query, cancellationToken).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<T> WithQueryBudgetAsync<T>(Func<ValueTask<T>> execute, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        var budget = StartQueryBudget(cancellationToken);
        try
        {
            var result = await execute().ConfigureAwait(false);
            if (budget.Spent >= QueryDeadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                throw new QueryCapacityException("query execution budget exceeded");
            }

            return result;
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
    // A streaming reader pauses the clock between SQLite steps, so the deadline
    // measures SQLite's work, not the consumer's time between rows; the instruction count accumulates.
    private QueryBudget StartQueryBudget(CancellationToken cancellationToken)
    {
        var budget = new QueryBudget(store.Time);
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
    private sealed class QueryBudget(TimeProvider time)
    {
        // Running since _started while _running; Spent adds the time before the last pause. Any timestamp, 0 included,
        // is a valid start.
        private bool _running = true;
        private long _started = time.GetTimestamp();
        private TimeSpan _paused;

        public int Callbacks { get; set; }

        public TimeSpan Spent => _running ? _paused + time.GetElapsedTime(_started) : _paused;

        public void Resume()
        {
            if (!_running)
            {
                _started = time.GetTimestamp();
                _running = true;
            }
        }

        public void Pause()
        {
            if (_running)
            {
                _paused += time.GetElapsedTime(_started);
                _running = false;
            }
        }
    }

    private void StopQueryBudget() => raw.sqlite3_progress_handler(session.Connection.Handle, 0, null, null);

    private static QueryCapacityException Exhausted(SqliteException error, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return new QueryCapacityException("query execution budget exceeded", error);
    }

    // Bound the candidate count and bytes before a temporary sort. The sorter carries only ids and keys;
    // octet_length reads body size metadata without loading the body.
    private async ValueTask CheckSortBudgetAsync(string collection, long maxBytes, CancellationToken cancellationToken)
    {
        var command = Uncached(collection);
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
                    if (++rows > 10000 || bytes > maxBytes)
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
                    var size = reader.GetInt64(2);
                    if (rows.Count == query.Limit || (rows.Count > 0 && bytes + size > query.MaxBytes))
                    {
                        return new QueryPage(rows, HasMore: true);
                    }

                    bytes += size;
                    var keys = new object?[reader.FieldCount - 3];
                    for (var i = 0; i < keys.Length; i++)
                    {
                        // The provider's text conversion stops at NUL. Read string key bytes with their
                        // SQLite length so a continuation carries the complete sort value.
                        keys[i] = reader.IsDBNull(3 + i) ? null
                            : i % 2 == 1 && reader.GetInt64(2 + i) == 5
                                ? Encoding.UTF8.GetString(reader.GetFieldValue<byte[]>(3 + i))
                                : reader.GetValue(3 + i);
                    }

                    // Bodies are fetched only after the id/key page admits this row; a temporary sort never carries them.
                    var document = await GetAsync(query.Collection, reader.GetString(0), cancellationToken).ConfigureAwait(false);
                    rows.Add(new QueryRow(document!, keys));
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
                        rows.Add(new QueryRow(new StoredDocument(id, reader.GetInt64(2), ReadShapeJson(reader, 3), Incarnation), []));
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
            await foreach (var document in ReadBudgetedAsync(command, cancellationToken).ConfigureAwait(false))
            {
                yield return document;
            }
        }
    }

    // Streams a command's (id, version, body) rows; a null body is a tombstone. The query budget covers the whole
    // enumeration, including a scan or sort SQLite does before the first row, and the handler goes even when the consumer
    // stops early.
    private async IAsyncEnumerable<StoredDocument> ReadBudgetedAsync(
        SqliteCommand command, [EnumeratorCancellation] CancellationToken cancellationToken, SqlQuery? query = null)
    {
        var budget = StartQueryBudget(cancellationToken);
        try
        {
            SqliteDataReader reader;
            try
            {
                if (query is { Order.Count: > 0 })
                {
                    var plan = await ReadSqlPlanAsync(query, cancellationToken).ConfigureAwait(false);
                    if (plan.Steps.Any(step => step.Contains("TEMP B-TREE", StringComparison.Ordinal)))
                    {
                        await CheckSortBudgetAsync(query.Collection, 8 * 1024 * 1024, cancellationToken).ConfigureAwait(false);
                    }
                }

                reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                budget.Pause();
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
                    }
                    catch (SqliteException error) when (error.SqliteErrorCode == raw.SQLITE_INTERRUPT)
                    {
                        throw Exhausted(error, cancellationToken);
                    }
                    finally
                    {
                        budget.Pause();
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

                    if (query is not null)
                    {
                        StoredDocument? document;
                        try
                        {
                            budget.Resume();
                            document = await GetAsync(query.Collection, reader.GetString(0), cancellationToken).ConfigureAwait(false);
                        }
                        catch (SqliteException error) when (error.SqliteErrorCode == raw.SQLITE_INTERRUPT)
                        {
                            throw Exhausted(error, cancellationToken);
                        }
                        finally
                        {
                            budget.Pause();
                        }

                        if (budget.Spent >= QueryDeadline)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            throw new QueryCapacityException("query execution budget exceeded");
                        }

                        yield return document!;
                        continue;
                    }

                    // Only ChangesAsync reads rows directly: (id, version, body, history offset).
                    yield return reader.IsDBNull(2)
                        ? new StoredDocument(reader.GetString(0), reader.GetInt64(1), ReadOnlyMemory<byte>.Empty, Incarnation, Deleted: true, HistoryOffset: reader.GetInt64(3))
                        : new StoredDocument(reader.GetString(0), reader.GetInt64(1), ReadShapeJson(reader, 2), Incarnation, HistoryOffset: reader.GetInt64(3));
                }
            }
        }
        finally
        {
            StopQueryBudget();
        }
    }

    public async ValueTask<bool> CreateCollectionAsync(CollectionInfo collection, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(collection);
        ThrowIfReadOnly();
        var command = Command(SqliteStatements.CreateCollection, collection.Name);
        command.Parameters.AddWithValue("$created", collection.CreatedAt);
        try
        {
            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 0)
            {
                return false;
            }

            _metadataChanged = true;
            // Local metadata allocates immediately, so a failed allocation faults this mutation too.
            // Apply has already pinned its starting head and will allocate its shared history at commit.
            if (_importedFromOffset is null)
            {
                await OffsetAsync(cancellationToken).ConfigureAwait(false);
            }

            return true;
        }
        catch
        {
            _faulted = true;
            throw;
        }
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
        ThrowIfReadOnly();
        DocumentMutation(replicated: false);
        try
        {
            if (_changes is not null)
            {
                // Release every pending body in this collection before admitting uncaptured deletions.
                // The final tombstone group must fit independently of the stored ids' enumeration order.
                for (var index = 0; index < _changes.Count; index++)
                {
                    var pending = _changes[index];
                    if (pending.Collection == name)
                    {
                        _changeBytes -= pending.Json?.Length ?? 0;
                        _changes[index] = pending with { Version = await OffsetAsync(cancellationToken).ConfigureAwait(false), Json = null };
                    }
                }

                // Include tombstones, read only ids, and stop after the first row that cannot fit.
                var command = Command(SqliteStatements.CollectionDocumentIds, name);
                command.Parameters.AddWithValue("$limit", IStorageTransaction.MaxChangeGroupChanges + 1);
                var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                await using (reader.ConfigureAwait(false))
                {
                    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    {
                        // Inspect the stored size before materializing an id; replacement states are checked by Capture.
                        if ((long)Encoding.UTF8.GetByteCount(name) + reader.GetInt64(1) > IStorageTransaction.MaxChangeGroupBytes)
                        {
                            throw new ChangeGroupCapacityException();
                        }

                        var offset = await OffsetAsync(cancellationToken).ConfigureAwait(false);
                        Capture(new DocumentChange(name, reader.GetString(0), offset, null));
                    }
                }
            }

            // ponytail: one transaction deletes the collection; large captured drops fail their group bounds.
            // History first: the dropped generation is found through the collection row.
            await Command(SqliteStatements.DropHistory, name).ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await Command(SqliteStatements.DropDocuments, name).ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            if (await Command(SqliteStatements.DropCollection, name).ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 0)
            {
                return false;
            }

            await WriteAsync(_ => Task.CompletedTask, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch
        {
            _faulted = true;
            throw;
        }
    }

    public async ValueTask<long> CommitAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (_faulted)
        {
            throw new InvalidOperationException("an operation of this transaction failed, so it can only be rolled back");
        }

        long? sequence = null;
        long offset;
        try
        {
            if (_importedFromOffset is { } previous && (_appliedChanges.Count > 0 || _metadataChanged))
            {
                // One local history offset preserves the original versions of every winning import.
                // Imported history may use the reserved tail beyond the local merge-version ceiling.
                _offset = Math.Max(checked(previous + 1), await GetOffsetAsync(cancellationToken).ConfigureAwait(false));
                var observed = Command(SqliteStatements.ObserveOffset);
                observed.Parameters.AddWithValue("$version", _offset.Value);
                await observed.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                foreach (var change in _appliedChanges)
                {
                    await AppendChangeAsync(change.Collection, change.Id, _offset.Value, change.Version,
                        change.Json is { } json ? StrictUtf8.GetString(json.Span) : null, cancellationToken).ConfigureAwait(false);
                }
            }

            offset = _offset ?? await GetOffsetAsync(cancellationToken).ConfigureAwait(false);
            if (_changes is { Count: > 0 })
            {
                sequence = await AppendChangesAsync(_changes, cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            _faulted = true;
            throw;
        }

        // Imported clock progress wakes shape waiters even when every document state lost the merge.
        if (_offset is not null || (_importedFromOffset is { } previousHead && offset > previousHead))
        {
            committed?.Invoke();
        }

        if (sequence is { } appended)
        {
            changesCommitted!(appended);
        }

        return offset;
    }

    public async ValueTask<ReadOnlyMemory<byte>> GetOriginAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        return await session.ExecuteAsync<ReadOnlyMemory<byte>>(async () =>
            (byte[])(await Command(SqliteStatements.Origin).ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<long> GetLastChangeSequenceAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        return await session.ExecuteAsync(async () =>
            (long)(await Command(SqliteStatements.LastSequence).ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<IReadOnlyList<DocumentChange>?> ReadChangesAsync(long sequence, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var command = Command(SqliteStatements.ReadChanges);
        command.Parameters.AddWithValue("$sequence", sequence);
        command.Parameters.AddWithValue("$limit", IStorageTransaction.MaxChangeGroupChanges + 1);
        command.Parameters.AddWithValue("$maxBytes", IStorageTransaction.MaxChangeGroupBytes);
        var changes = await session.ExecuteAsync(async () => await ReadChangesAsync(command, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
        return changes.Count == 0 ? null : changes;
    }

    public async ValueTask<ReadOnlyMemory<byte>?> GetChangeDigestAsync(long sequence, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ArgumentOutOfRangeException.ThrowIfNegative(sequence);
        if (sequence == 0)
        {
            return JournalDigest.Initial((await GetOriginAsync(cancellationToken).ConfigureAwait(false)).Span);
        }

        var command = Command(SqliteStatements.GetChangeDigest);
        command.Parameters.AddWithValue("$sequence", sequence);
        return await session.ExecuteAsync(async () => await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is byte[] digest
            ? digest.Length == JournalDigest.Length ? (ReadOnlyMemory<byte>?)digest : throw new InvalidDataException("the stored journal digest is invalid")
            : null, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<bool> ApplyAsync(DocumentChange change, CancellationToken cancellationToken)
    {
        ThrowIfReadOnly();
        DocumentMutation(replicated: true);
        try
        {
            ArgumentNullException.ThrowIfNull(change);
            if (Shaped && !DocumentChange.HasValidKey(change.Collection, change.Id))
            {
                throw new InvalidDataException("a replicated change has an invalid collection or id");
            }

            _importedFromOffset ??= _offset is { } metadataOffset
                ? metadataOffset - 1
                : await GetOffsetAsync(cancellationToken).ConfigureAwait(false);
            // Every direct Apply in this transaction uses the same starting clock, so several
            // individually small advances cannot chain past the whole-group admission limit.
            VersionClock.ValidateImport(change.Version, _importedFromOffset.Value);
            var bytes = ContentBytes(change);
            if (_importedCount == IStorageTransaction.MaxChangeGroupChanges
                || bytes > IStorageTransaction.MaxChangeGroupBytes - _importedBytes)
            {
                throw new InvalidOperationException(
                    $"a replicated transaction holds at most {IStorageTransaction.MaxChangeGroupChanges} changes and {IStorageTransaction.MaxChangeGroupBytes} bytes");
            }

            var admittedBody = change.Json is { } incoming ? Admit(incoming) : null;
            if (Shaped)
            {
                // Bound the complete imported commit, including shape framing.
                var historyBytes = (change.Json?.Length ?? 0) + (long)JsonEncodedText.Encode(change.Id).EncodedUtf8Bytes.Length + OpFramingBytes;
                if (_writtenBytes + historyBytes > MaxWrittenBytes)
                {
                    throw new StorageWriteLimitException("a replicated transaction's shape history fits at most 7 MiB with framing");
                }

                _writtenBytes += historyBytes;
                // Own the bytes before awaiting SQL so the stored state and its deferred history cannot diverge.
                change = change with { Json = change.Json is { } body ? body.ToArray() : (ReadOnlyMemory<byte>?)null };
            }

            _importedCount++;
            _importedBytes += bytes;
            // Advance even for a losing state: a subsequent local transaction must supersede what was observed.
            var offset = Command(SqliteStatements.ObserveOffset);
            offset.Parameters.AddWithValue("$version", change.Version);
            await offset.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            var retained = Command(SqliteStatements.RetainedDeletion, change.Collection);
            retained.Parameters.AddWithValue("$id", change.Id);
            retained.Parameters.AddWithValue("$version", change.Version);
            if (await retained.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null)
            {
                return false;
            }

            // Replication carries document state, not collection metadata; provide the addressed collection.
            await CreateCollectionAsync(new CollectionInfo(change.Collection, 0), cancellationToken).ConfigureAwait(false);
            bool changed;
            if (change.Json is { } json)
            {
                var document = BodyCommand(SqliteStatements.ApplyDocument, change.Collection, change.Id);
                document.Parameters.AddWithValue("$version", change.Version);
                document.Parameters.AddWithValue("$body", admittedBody!);
                changed = await document.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
            }
            else
            {
                var deletion = Command(SqliteStatements.ApplyTombstone, change.Collection);
                deletion.Parameters.AddWithValue("$id", change.Id);
                deletion.Parameters.AddWithValue("$version", change.Version);
                changed = await deletion.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
            }

            if (changed && Shaped)
            {
                _appliedChanges.Add(change);
            }

            return changed;
        }
        catch
        {
            _faulted = true;
            throw;
        }
    }

    public async ValueTask<long> GetAppliedSequenceAsync(ReadOnlyMemory<byte> origin, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var command = Command(SqliteStatements.GetApplied);
        command.Parameters.AddWithValue("$origin", origin.ToArray());
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as long? ?? 0;
    }

    public async ValueTask<(long Sequence, ReadOnlyMemory<byte> Digest)> GetAppliedFrontierAsync(
        ReadOnlyMemory<byte> origin, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var initial = JournalDigest.Initial(origin.Span);
        var command = Command(SqliteStatements.GetAppliedFrontier);
        command.Parameters.AddWithValue("$origin", origin.ToArray());
        var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await using (reader.ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) || reader.IsDBNull(1))
            {
                return (0, initial);
            }

            var sequence = reader.GetInt64(0);
            var digest = reader.GetFieldValue<byte[]>(1);
            return sequence > 0 && digest.Length == JournalDigest.Length
                ? (sequence, digest)
                : throw new InvalidDataException("the stored applied frontier is invalid");
        }
    }

    public ValueTask SetAppliedSequenceAsync(ReadOnlyMemory<byte> origin, long sequence, CancellationToken cancellationToken) =>
        SetAppliedAsync(origin, sequence, digest: null, cancellationToken);

    public ValueTask SetAppliedFrontierAsync(
        ReadOnlyMemory<byte> origin, long sequence, ReadOnlyMemory<byte> digest, CancellationToken cancellationToken) =>
        SetAppliedAsync(origin, sequence, digest, cancellationToken);

    private async ValueTask SetAppliedAsync(
        ReadOnlyMemory<byte> origin, long sequence, ReadOnlyMemory<byte>? digest, CancellationToken cancellationToken)
    {
        ThrowIfReadOnly();
        try
        {
            if (origin.Length != 16 || sequence <= 0 || (digest is { } proof && proof.Length != JournalDigest.Length))
            {
                throw new InvalidDataException("an applied frontier requires a valid origin, sequence and digest");
            }

            var command = Command(SqliteStatements.SetApplied);
            command.Parameters.AddWithValue("$origin", origin.ToArray());
            command.Parameters.AddWithValue("$sequence", sequence);
            command.Parameters.AddWithValue("$digest", digest is { } bytes ? bytes.ToArray() : DBNull.Value);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            _faulted = true;
            throw;
        }
    }

    /// <summary>Whether the change log covers every document and tombstone of this database.</summary>
    internal async Task<bool> IsCompleteAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        return (long)(await Command(SqliteStatements.Complete).ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))! != 0;
    }

    internal async Task MarkCompleteAsync(CancellationToken cancellationToken)
    {
        ThrowIfReadOnly();
        try
        {
            await Command(SqliteStatements.MarkComplete).ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            _faulted = true;
            throw;
        }
    }

    internal async Task<int> CountOversizedBaselineKeysAsync(CancellationToken cancellationToken) =>
        checked((int)(long)(await Command(SqliteStatements.CountOversizedBaselineKeys).ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!);

    /// <summary>Appends one bounded baseline chunk; inadmissible pre-existing states are skipped and counted.</summary>
    internal async Task<BaselineChunk?> AppendBaselineAsync(bool tombstones, (string Collection, string Id)? after, CancellationToken cancellationToken)
    {
        ThrowIfReadOnly();
        try
        {
            return await AppendBaselineChunkAsync(tombstones, after, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            _faulted = true;
            throw;
        }
    }

    private async Task<BaselineChunk?> AppendBaselineChunkAsync(bool tombstones, (string Collection, string Id)? after, CancellationToken cancellationToken)
    {
        var command = Command(
            (tombstones, after is null) switch
            {
                (false, true) => SqliteStatements.BaselineDocumentsFirst,
                (false, false) => SqliteStatements.BaselineDocumentsAfter,
                (true, true) => SqliteStatements.BaselineTombstonesFirst,
                (true, false) => SqliteStatements.BaselineTombstonesAfter,
            },
            after?.Collection);
        if (after is { } cursor)
        {
            command.Parameters.AddWithValue("$id", cursor.Id);
        }

        command.Parameters.AddWithValue("$limit", IStorageTransaction.MaxChangeGroupChanges);
        if (!tombstones)
        {
            command.Parameters.AddWithValue("$maxBytes", DocumentJson.MaxBytes);
        }

        var group = new List<DocumentChange>();
        (string Collection, string Id)? last = null;
        var skipped = 0;
        var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await using (reader.ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                // Read the stored byte length before materializing the body; the query omits oversized bodies.
                var rowKey = (Collection: reader.GetString(0), Id: reader.GetString(1));
                var bodyBytes = reader.GetInt64(4);
                var bytes = (long)Encoding.UTF8.GetByteCount(rowKey.Collection) + Encoding.UTF8.GetByteCount(rowKey.Id) + bodyBytes;
                if (bodyBytes > DocumentJson.MaxBytes || !DocumentChange.HasValidKey(rowKey.Collection, rowKey.Id)
                    || reader.GetInt64(2) is <= 0 or > VersionClock.MaxVersion || bytes > IStorageTransaction.MaxChangeGroupBytes)
                {
                    skipped++;
                }
                else if (_changeBytes + bytes > IStorageTransaction.MaxChangeGroupBytes)
                {
                    break;
                }
                else
                {
                    var change = Change(reader);
                    try
                    {
                        if (change.Json is { } json)
                        {
                            // A legacy rejection skips this row without faulting the seeding transaction.
                            _ = DocumentJson.Read(json, requireObject: true);
                        }

                        group.Add(change);
                        _changeBytes += bytes;
                    }
                    catch (DocumentValidationException)
                    {
                        skipped++;
                    }
                }

                last = rowKey;
            }
        }

        if (last is not { } key)
        {
            return null;
        }

        var sequence = group.Count == 0 ? 0 : await AppendChangesAsync(group, cancellationToken).ConfigureAwait(false);
        return new BaselineChunk(key, sequence, skipped);
    }

    public async ValueTask DisposeAsync()
    {
        // Return the session exactly once, even if a later transaction has already rented it.
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        // Failed rollback or read-only reset closes the session instead of reusing it.
        try
        {
            foreach (var command in _bodyCommands.Values)
            {
                command.Parameters.Clear();
                await command.DisposeAsync().ConfigureAwait(false);
            }

            _bodyCommands.Clear();
            _changes?.Clear();
            _appliedChanges.Clear();
            await transaction.DisposeAsync().ConfigureAwait(false);
            if (!write)
            {
                var writable = session.Connection.CreateCommand();
                await using (writable.ConfigureAwait(false))
                {
                    writable.CommandText = "PRAGMA query_only = OFF;";
                    await writable.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
                }
            }
        }
        catch
        {
            store.Discard(session);
            throw;
        }

        store.Return(session);
    }

    private static byte[] ReadShapeJson(SqliteDataReader reader, int ordinal)
    {
        var json = reader.GetFieldValue<byte[]>(ordinal);
        DocumentJson.ValidateStored(json);
        return json;
    }

    private static DocumentChange Change(SqliteDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        reader.GetInt64(2),
        // Typed as the nullable memory itself: a null byte[] would convert to empty memory, not to a deletion.
        reader.IsDBNull(3) ? null : (ReadOnlyMemory<byte>?)reader.GetFieldValue<byte[]>(3));

    private static long ContentBytes(DocumentChange change) =>
        (long)Encoding.UTF8.GetByteCount(change.Collection) + Encoding.UTF8.GetByteCount(change.Id) + (change.Json?.Length ?? 0);

    internal static async Task<List<DocumentChange>> ReadChangesAsync(SqliteCommand command, CancellationToken cancellationToken)
    {
        var changes = new List<DocumentChange>();
        long bytes = 0;
        var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await using (reader.ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                bytes += reader.GetInt64(4);
                if (changes.Count >= IStorageTransaction.MaxChangeGroupChanges || bytes > IStorageTransaction.MaxChangeGroupBytes)
                {
                    throw new InvalidDataException("the stored change group exceeds the journal limits");
                }

                changes.Add(Change(reader));
            }
        }

        return changes;
    }

    // Capacity errors occur during the mutation, before copying JSON, as well as at commit.
    // The mutator marks the transaction rollback-only even when its caller catches the error.
    private void CheckCaptureCapacity(long bytes, bool replacing = false)
    {
        if ((!replacing && _changes!.Count >= IStorageTransaction.MaxChangeGroupChanges)
            || bytes > IStorageTransaction.MaxChangeGroupBytes - _changeBytes)
        {
            throw new ChangeGroupCapacityException(
                $"a replicated transaction holds at most {IStorageTransaction.MaxChangeGroupChanges} changes and {IStorageTransaction.MaxChangeGroupBytes} bytes");
        }
    }

    private void Capture(DocumentChange change)
    {
        if (!DocumentChange.HasValidKey(change.Collection, change.Id))
        {
            throw new InvalidDataException("a captured change has an invalid collection or id");
        }

        // ponytail: scan at most 1024 states; use a keyed cache if capture profiling warrants it.
        var index = _changes!.FindIndex(previous => previous.Collection == change.Collection && previous.Id == change.Id);
        var bytes = ContentBytes(change) - (index >= 0 ? ContentBytes(_changes[index]) : 0);
        CheckCaptureCapacity(bytes, replacing: index >= 0);
        // Reserve only the final state, before copying it; shape history still counts every mutation.
        var owned = change with { Json = change.Json is { } json ? json.ToArray() : (ReadOnlyMemory<byte>?)null };
        if (index >= 0)
        {
            _changes[index] = owned;
        }
        else
        {
            _changes.Add(owned);
        }

        _changeBytes += bytes;
    }

    // Appends one change group and returns its sequence. The next sequence is read inside this write
    // transaction, which SQLite serializes per database.
    private async Task<long> AppendChangesAsync(List<DocumentChange> changes, CancellationToken cancellationToken)
    {
        if (changes.Count > IStorageTransaction.MaxChangeGroupChanges || _changeBytes > IStorageTransaction.MaxChangeGroupBytes)
        {
            throw new ChangeGroupCapacityException(
                $"a replicated transaction holds at most {IStorageTransaction.MaxChangeGroupChanges} changes and {IStorageTransaction.MaxChangeGroupBytes} bytes");
        }

        var sequence = await Command(SqliteStatements.NextSequence).ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is long next
            ? next
            : throw new OverflowException("the change sequence is exhausted");
        var previous = await GetChangeDigestAsync(sequence - 1, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("the preceding journal digest is missing");
        var digest = JournalDigest.Compute((await GetOriginAsync(cancellationToken).ConfigureAwait(false)).Span,
            sequence, previous.Span, changes);
        var ordinal = 0;
        foreach (var change in changes)
        {
            var append = BodyCommand(SqliteStatements.AppendReplicationChange, change.Collection, change.Id);
            append.Parameters.AddWithValue("$sequence", sequence);
            append.Parameters.AddWithValue("$ordinal", ordinal++);
            append.Parameters.AddWithValue("$version", change.Version);
            append.Parameters.AddWithValue("$body", change.Json is { } json ? Encoding.UTF8.GetString(json.Span) : DBNull.Value);
            await append.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var prefix = Command(SqliteStatements.AppendChangeDigest);
        prefix.Parameters.AddWithValue("$sequence", sequence);
        prefix.Parameters.AddWithValue("$digest", digest);
        await prefix.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        return sequence;
    }

    // Every write of one transaction shares one offset, allocated under the database write lock.
    private async ValueTask<long> OffsetAsync(CancellationToken cancellationToken)
    {
        if (_offset is null)
        {
            VersionClock.EnsureCanAllocate(await GetOffsetAsync(cancellationToken).ConfigureAwait(false));
        }

        _offset ??= await session.Command(SqliteStatements.NextOffset, transaction).ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is long offset
            ? offset
            : throw new OverflowException("the commit offset is exhausted");
        return _offset.Value;
    }

    // After disposal the session may already belong to another transaction: refuse before touching it.
    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    // Check calls before no-op detection too, independently of whether this database captures changes.
    private void DocumentMutation(bool replicated)
    {
        if (_replicatedMutation is { } previous && previous != replicated)
        {
            _faulted = true;
            throw new InvalidOperationException("a transaction cannot mix local document mutations and replicated changes");
        }

        _replicatedMutation = replicated;
    }

    private SqliteCommand Command(string sql, string? collection = null)
    {
        var command = session.Command(sql, transaction);
        if (collection is not null)
        {
            command.Parameters.AddWithValue("$collection", collection);
        }

        return command;
    }

    private SqliteCommand BodyCommand(string sql, string collection, string id)
    {
        if (!_bodyCommands.TryGetValue(sql, out var command))
        {
            command = session.Connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            _bodyCommands.Add(sql, command);
        }

        command.Parameters.Clear();
        command.Parameters.AddWithValue("$collection", collection);
        command.Parameters.AddWithValue("$id", id);
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

/// <summary>Last baseline key, appended sequence (0 when all rows were skipped), and inadmissible rows skipped.</summary>
internal readonly record struct BaselineChunk((string Collection, string Id) Last, long Sequence, int Skipped);

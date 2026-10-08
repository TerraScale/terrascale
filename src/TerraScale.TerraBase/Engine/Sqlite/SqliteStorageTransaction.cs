using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using SQLitePCL;
using TerraBase.Features.Memory;
using TerraBase.Primitives;

namespace TerraBase.Engine.Sqlite;

/// <summary>
/// One transaction; every SQLite call runs on its runner: the <see cref="SqliteIoPool"/> for reads, the database's
/// <see cref="SqliteWriteLane"/> for writes.
/// </summary>
/// <param name="store">The database this transaction belongs to.</param>
/// <param name="session">The rented session; returned to <paramref name="store"/> on dispose.</param>
/// <param name="transaction">The SQLite transaction.</param>
/// <param name="runner">Runs every SQLite call of this transaction.</param>
/// <param name="write">Whether mutations are allowed.</param>
/// <param name="durable">Whether the session runs with <c>synchronous = FULL</c> until disposal resets it to <c>NORMAL</c>.</param>
/// <param name="committed">Runs after a commit advances the local shape history or its head.</param>
/// <param name="changesCommitted">When set, local mutations are captured and it receives the group's sequence after commit.</param>
/// <param name="ownsAdmission">Whether disposal releases this transaction's I/O admission.</param>
internal sealed partial class SqliteStorageTransaction(
    SqliteDatabaseStore store, SqliteSession session, SqliteTransaction transaction, SqliteRunner runner, bool write, bool durable, Action? committed,
    Action<long>? changesCommitted, bool ownsAdmission)
    : IStorageTransaction
{
    // One commit must fit one shape log chunk (8 MiB body), including every repeated write and its framing.
    private const long MaxWrittenBytes = 7 * 1024 * 1024;
    private const int MaxDocumentBytes = DocumentJson.MaxBytes;
    private const int MaxWrites = 1024;
    private const int MaxIdBytes = 1024;
    private const int OpFramingBytes = 128;
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    // Serializes calls and disposal, so two workers never use the same connection concurrently.
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Lock _disposalGate = new();
    private Task? _disposal;
    private bool _streaming;

    private readonly List<DocumentChange>? _changes = changesCommitted is null ? null : [];

    // Where each captured document's latest state sits in _changes, so a capture finds its predecessor in O(1).
    private readonly Dictionary<(string Collection, string Id), int> _changeIndex = [];

    // Captured states a batched write replaced since its savepoint, to put back if it rolls back.
    private readonly List<(int Index, DocumentChange Previous)> _replacedSinceSavepoint = [];

    // Body statements live only for this transaction: idle sessions must not retain bound JSON.
    private readonly Dictionary<string, SqliteCommand> _bodyCommands = [with(StringComparer.Ordinal)];

    // Winning imports wait for commit so the whole group shares one local history offset.
    private readonly List<DocumentChange> _appliedChanges = [];
    private long? _importedFromOffset;
    private bool _metadataChanged;
    private int _importedCount;
    private long _importedBytes;
    private long _changeBytes;
    private bool? _replicatedMutation;

    // A refused or failed write makes this transaction able only to roll back.
    private bool _faulted;
    private int _writes;
    private long _writtenBytes;

    // This transaction's local commit offset, separate from incoming document merge versions.
    private long? _offset;

    // Whether this transaction created or dropped a collection: the store forgets its collection names on commit.
    private bool _collectionsChanged;

    // Group commit (SqliteDatabaseStore.WriteAsync): each queued write runs inside a savepoint, and this snapshot of the
    // in-memory capture and limits rolls back with it.
    private (int Changes, long ChangeBytes, int Writes, long WrittenBytes, long? Offset, bool? ReplicatedMutation, bool CollectionsChanged)?
        _savepoint;

    /// <summary>Whether a per-commit limit refused a write since the last savepoint: alone in a transaction, it could fit.</summary>
    internal bool LimitsReached { get; private set; }

    // Taken when the transaction begins: a catalog follower adopting its primary's incarnation never mixes it into a
    // snapshot read before.
    public Guid Scope { get; } = store.Scope;

    public ValueTask<StoredDocument?> GetAsync(string collection, string id, CancellationToken cancellationToken) =>
        RunAsync(() => ReadDocument(collection, id), cancellationToken);

    // Worker-only: query readers fetch bodies after their id/key page has admitted the row.
    private StoredDocument? ReadDocument(string collection, string id)
    {
        var command = Command(SqliteStatements.Get, collection);
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        return reader.Read() && !reader.IsDBNull(1)
            ? new StoredDocument(id, reader.GetInt64(0), reader.GetFieldValue<byte[]>(1), Scope)
            : null;
    }

    public async ValueTask<DocumentVersion?> GetVersionAsync(string collection, string id, CancellationToken cancellationToken)
    {
        return await RunAsync<DocumentVersion?>(() =>
        {
            var command = Command(SqliteStatements.Version, collection);
            command.Parameters.AddWithValue("$id", id);
            using var reader = command.ExecuteReader();
            return reader.Read() ? new DocumentVersion(reader.GetInt64(0), reader.GetBoolean(1)) : null;
        }, cancellationToken).ConfigureAwait(false);
    }

    // Worker-only; runs inside a mutation, so a rejected document also makes the transaction roll back only.
    private string Admit(ReadOnlyMemory<byte> json) =>
        // The private system catalog also stores scalar indexes; tenant documents must be objects.
        DocumentJson.Read(json, requireObject: store.Tenant is not null);

    public async ValueTask<long> PutAsync(string collection, string id, ReadOnlyMemory<byte> json, CancellationToken cancellationToken)
    {
        return await RunAsync(() =>
        {
            ThrowIfReadOnly();
            DocumentMutation(replicated: false);
            var body = Admit(json);
            using var schemaReservation = Shaped ? ReserveCollectionMetadata(collection) : null;
            if (Shaped && ReadCollectionSchema(collection) is { } schema)
            {
                CollectionSchemaValidation.Validate(schema, json, cancellationToken, store.SchemaMemory, store.SchemaEvaluations);
            }

            Reserve(collection, id, json.Length);
            return Write(offset =>
            {
                if (_changes is not null)
                {
                    Capture(new DocumentChange(collection, id, offset, json));
                }

                var put = BodyCommand(SqliteStatements.Put, collection, id);
                put.Parameters.AddWithValue("$version", offset);
                put.Parameters.AddWithValue("$body", body);
                put.ExecuteNonQuery();
                AppendChange(collection, id, offset, offset, body, cancellationToken);
            }, cancellationToken);
        }, cancellationToken, mutation: true).ConfigureAwait(false);
    }

    public async ValueTask<bool> DeleteAsync(string collection, string id, CancellationToken cancellationToken)
    {
        return await RunAsync(() =>
        {
            ThrowIfReadOnly();
            DocumentMutation(replicated: false);
            var command = Command(SqliteStatements.Delete, collection);
            command.Parameters.AddWithValue("$id", id);
            if (command.ExecuteNonQuery() == 0)
            {
                return false;
            }

            // Physical removal remains available internally; history and peers still need its deletion.
            if (Shaped || _changes is not null)
            {
                Reserve(collection, id, 0);
                Write(offset =>
                {
                    if (_changes is not null)
                    {
                        Capture(new DocumentChange(collection, id, offset, null));
                    }

                    AppendChange(collection, id, offset, offset, null, cancellationToken);
                }, cancellationToken);
            }

            return true;
        }, cancellationToken, mutation: true).ConfigureAwait(false);
    }

    public async ValueTask<long?> TombstoneAsync(string collection, string id, CancellationToken cancellationToken)
    {
        return await RunAsync<long?>(() =>
        {
            ThrowIfReadOnly();
            DocumentMutation(replicated: false);
            // Check liveness from metadata without reading a body, within this serialized call.
            var version = Command(SqliteStatements.Version, collection);
            version.Parameters.AddWithValue("$id", id);
            using (var reader = version.ExecuteReader())
            {
                if (!reader.Read() || reader.GetBoolean(1))
                {
                    return null;
                }
            }

            Reserve(collection, id, 0);
            // ponytail: keep tombstones until replication defines when they may be reaped.
            return Write(offset =>
            {
                if (_changes is not null)
                {
                    Capture(new DocumentChange(collection, id, offset, null));
                }

                var command = Command(SqliteStatements.Tombstone, collection);
                command.Parameters.AddWithValue("$id", id);
                command.Parameters.AddWithValue("$version", offset);
                command.ExecuteNonQuery();
                AppendChange(collection, id, offset, offset, null, cancellationToken);
            }, cancellationToken);
        }, cancellationToken, mutation: true).ConfigureAwait(false);
    }

    // Client commits keep shape history and its limits; _system is never a client database.
    private bool Shaped => !store.IsSystem;

    private long Write(Action<long> apply, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var offset = Offset();
        apply(offset);
        return offset;
    }

    private void AppendChange(string collection, string id, long offset, long documentVersion, string? body, CancellationToken cancellationToken)
    {
        if (!Shaped)
        {
            return;
        }

        if (Append() == 1)
        {
            return;
        }

        // Engine callers may omit collection creation; its fresh generation keeps their history.
        var create = Command(SqliteStatements.CreateCollection, collection);
        create.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        create.Parameters.AddWithValue("$entityId", EntityIds.New().ToString());
        cancellationToken.ThrowIfCancellationRequested();
        create.ExecuteNonQuery();
        _collectionsChanged = true;
        if (Append() != 1)
        {
            throw new InvalidOperationException("a document write needs its collection's metadata for shape history");
        }

        int Append()
        {
            cancellationToken.ThrowIfCancellationRequested();
            var command = Command(SqliteStatements.AppendChange, collection);
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$version", offset);
            command.Parameters.AddWithValue("$documentVersion", documentVersion);
            command.Parameters.AddWithValue("$body", (object?)body ?? DBNull.Value);
            return command.ExecuteNonQuery();
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
            throw new InvalidDataException("a captured change has an invalid collection or id");
        }

        var bytes = documentBytes + (long)JsonEncodedText.Encode(id).EncodedUtf8Bytes.Length + OpFramingBytes;
        // An empty id has no successor to resume after; no writer ever produced one (schema 1 required 1 to 256 characters).
        if (id.Length == 0 || documentBytes > MaxDocumentBytes || Encoding.UTF8.GetByteCount(id) > MaxIdBytes || _writes == MaxWrites
            || _writtenBytes + bytes > MaxWrittenBytes)
        {
            LimitsReached = _writes > 0;
            throw new StorageWriteLimitException(
                "a transaction writes at most 1024 documents of at most 1 MiB, 7 MiB in total with framing, with ids of 1 to 1024 bytes");
        }

        _writes++;
        _writtenBytes += bytes;
    }

    public async ValueTask<DocumentPage> ListAsync(
        string collection, string? afterId, int limit, long maxBytes, CancellationToken cancellationToken)
    {
        return await RunAsync(() => WithQueryBudget(() =>
        {
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
            using var reader = command.ExecuteReader();
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!reader.Read())
                {
                    break;
                }

                var size = reader.GetInt64(3);
                if (documents.Count == limit || (documents.Count > 0 && bytes + size > maxBytes))
                {
                    return new DocumentPage(documents, HasMore: true);
                }

                bytes += size;
                documents.Add(new StoredDocument(reader.GetString(0), reader.GetInt64(1), reader.GetFieldValue<byte[]>(2), Scope));
            }

            return new DocumentPage(documents, HasMore: false);
        }, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<QueryPage> QueryAsync(DocumentQuery query, CancellationToken cancellationToken, QueryObservation? observation = null)
    {
        ArgumentNullException.ThrowIfNull(query);
        return await RunAsync(() => WithQueryBudget(() =>
        {
            if (query.Sort.Count > 0 && !query.Sort[0].Path.IsId)
            {
                CheckSortBudget(query.Collection, query.MaxBytes, cancellationToken, observation);
            }

            return ReadQuery(query, observation, cancellationToken);
        }, cancellationToken, observation), cancellationToken).ConfigureAwait(false);
    }

    // Runs only inside a pool item: the native progress callback and its removal own this connection.
    private T WithQueryBudget<T>(Func<T> execute, CancellationToken cancellationToken, QueryObservation? observation = null)
    {
        session.ObserveQueries(observation);
        var budget = StartQueryBudget(cancellationToken);
        try
        {
            var result = execute();
            if (budget.Spent >= store.QueryBudget)
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
            session.ObserveQueries(null);
        }
    }

    private const int MaxSnapshotSteps = 10_000;
    private const int MaxSnapshotSkips = 50_000;

    // The handler and its removal run only on workers while this transaction owns the connection.
    private QueryBudget StartQueryBudget(CancellationToken cancellationToken)
    {
        var budget = new QueryBudget(store.Time);
        raw.sqlite3_progress_handler(session.Connection.Handle, 1000,
            _ => cancellationToken.IsCancellationRequested || ++budget.Callbacks >= 5000 || budget.Spent >= store.QueryBudget ? 1 : 0, null);
        return budget;
    }

    private void StopQueryBudget() => raw.sqlite3_progress_handler(session.Connection.Handle, 0, null, null);

    private static QueryCapacityException Exhausted(SqliteException error, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return new QueryCapacityException("query execution budget exceeded", error);
    }

    // A stream accumulates SQLite execution time, excluding worker queueing and consumer time between rows.
    private sealed class QueryBudget(TimeProvider time)
    {
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

    // Bound both candidate count and JSON bytes before SQLite retains bodies in its temporary sorter.
    private void CheckSortBudget(string collection, long maxBytes, CancellationToken cancellationToken, QueryObservation? observation = null)
    {
        using var command = Uncached(collection);
        command.CommandText = $"""
            SELECT octet_length(body) FROM documents INDEXED BY documents_live
            WHERE collection = $collection AND {(observation is not null ? "tb_query_scan() AND " : "")}body IS NOT NULL ORDER BY id LIMIT 10001;
            """;
        using var reader = command.ExecuteReader();
        long bytes = 0;
        var rows = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!reader.Read())
            {
                break;
            }

            bytes += reader.GetInt64(0);
            if (++rows > 10000 || bytes > maxBytes)
            {
                throw new QueryCapacityException("query sort budget exceeded") { SortBudget = true };
            }
        }
    }

    private QueryPage ReadQuery(DocumentQuery query, QueryObservation? observation, CancellationToken cancellationToken)
    {
        // Compiled per request so every new filter shape does not grow the session's command cache.
        using var command = Uncached(query.Collection);
        command.CommandText = new SqliteQueryCompiler(command.Parameters).Compile(query, observation is not null);
        var rows = new List<QueryRow>();
        long bytes = 0;
        using var reader = command.ExecuteReader();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!reader.Read())
            {
                break;
            }

            var size = reader.GetInt64(2);
            if (rows.Count == query.Limit || (rows.Count > 0 && bytes + size > query.MaxBytes))
            {
                return new QueryPage(rows, HasMore: true);
            }

            bytes += size;
            var keys = new object?[reader.FieldCount - 3];
            for (var i = 0; i < keys.Length; i++)
            {
                // Read string key bytes with SQLite's length, preserving NULs in continuation keys.
                keys[i] = reader.IsDBNull(3 + i) ? null
                    : i % 2 == 1 && reader.GetInt64(2 + i) == 5
                        ? Encoding.UTF8.GetString(reader.GetFieldValue<byte[]>(3 + i))
                        : reader.GetValue(3 + i);
            }

            rows.Add(new QueryRow(ReadDocument(query.Collection, reader.GetString(0))!, keys));
        }

        return new QueryPage(rows, HasMore: false);
    }

    public async ValueTask<(long Count, bool Complete)> CountAsync(
        string collection, DocumentFilter? filter, long maxExamined, CancellationToken cancellationToken, QueryObservation? observation = null)
    {
        return await RunAsync(() => WithQueryBudget(() =>
        {
            using var command = Uncached(collection);
            command.CommandText = new SqliteQueryCompiler(command.Parameters).CompileCount(filter, maxExamined, observation is not null);
            using var reader = command.ExecuteReader();
            cancellationToken.ThrowIfCancellationRequested();
            reader.Read();
            return (reader.GetInt64(1), reader.GetInt64(0) <= maxExamined);
        }, cancellationToken, observation), cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<SnapshotPage> SnapshotAsync(DocumentQuery query, long offset, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        return await RunAsync(() =>
        {
            long generation;
            using (var collection = Command(SqliteStatements.GetCollection, query.Collection).ExecuteReader())
            {
                if (!collection.Read())
                {
                    return new SnapshotPage([], HasMore: false, ResumeAfter: null);
                }

                generation = collection.GetInt64(2);
            }

            using var command = Uncached(query.Collection);
            // One streamed read of the births key in id order: each id that existed at the offset is a step with
            // its last write at the offset, so the cost follows the ids read, never the depth of their history.
            command.CommandText = new SqliteQueryCompiler(command.Parameters).CompileSnapshot(query.Filter, generation, offset);
            command.Parameters.AddWithValue("$after", query.After?.Id ?? "");
            var rows = new List<QueryRow>();
            long bytes = 0;
            // A row may run fewer instructions than the progress handler's interval, so the page also counts
            // its rows and checks the deadline between them.
            var budget = StartQueryBudget(cancellationToken);
            try
            {
                using var reader = command.ExecuteReader();
                // Steps count only ids that existed at the offset, so a replay examines the same ids and ends at
                // the same boundary; ids first written later are skips, bounded separately.
                string? examined = null;
                for (int steps = 0, skips = 0; ;)
                {
                    cancellationToken.ThrowIfCancellationRequested();
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

                    if (!reader.Read())
                    {
                        return new SnapshotPage(rows, HasMore: false, ResumeAfter: null);
                    }

                    if (budget.Spent >= store.QueryBudget)
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
                    rows.Add(new QueryRow(new StoredDocument(id, reader.GetInt64(2), ReadShapeJson(reader, 3), Scope), []));
                    examined = id;
                    // The last row may cross the byte limit; the caller trims the page to its serialized size.
                    if (bytes >= query.MaxBytes)
                    {
                        return new SnapshotPage(rows, HasMore: true, ResumeAfter: id);
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
        }, cancellationToken).ConfigureAwait(false);
    }

    public ValueTask<long> GetOffsetAsync(CancellationToken cancellationToken) =>
        RunAsync(() => (long?)session.Command(SqliteStatements.CurrentOffset, transaction).ExecuteScalar() ?? 0, cancellationToken);

    public ValueTask<long> GetHistoryFloorAsync(CancellationToken cancellationToken) =>
        RunAsync(() => (long)session.Command(SqliteStatements.HistoryFloor, transaction).ExecuteScalar()!, cancellationToken);

    public IAsyncEnumerable<StoredDocument> ChangesAsync(
        string collection, DocumentFilter? filter, long after, long through, CancellationToken cancellationToken) =>
        ReadBudgetedAsync(() =>
        {
            var command = Uncached(collection);
            command.CommandText = new SqliteQueryCompiler(command.Parameters).CompileChanges(filter, after, through);
            return command;
        }, cancellationToken);

    private async IAsyncEnumerable<StoredDocument> ReadBudgetedAsync(
        Func<SqliteCommand> createCommand, [EnumeratorCancellation] CancellationToken cancellationToken, SqlQuery? query = null,
        QueryObservation? observation = null)
    {
        // The stream owns the operation gate until its reader is closed, including an early consumer break.
        await EnterStreamAsync(cancellationToken).ConfigureAwait(false);
        SqliteCommand? command = null;
        SqliteDataReader? reader = null;
        QueryBudget? budget = null;
        try
        {
            await runner.RunAsync(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                session.ObserveQueries(observation);
                budget = StartQueryBudget(cancellationToken);
                command = createCommand();
                try
                {
                    if (query is { Order.Count: > 0 }
                        && ReadSqlPlan(query, cancellationToken).Steps.Any(step => step.Contains("TEMP B-TREE", StringComparison.Ordinal)))
                    {
                        CheckSortBudget(query.Collection, 8 * 1024 * 1024, cancellationToken, observation);
                    }

                    reader = command.ExecuteReader();
                }
                catch (SqliteException error) when (error.SqliteErrorCode == raw.SQLITE_INTERRUPT)
                {
                    throw Exhausted(error, cancellationToken);
                }
                finally
                {
                    budget.Pause();
                }
            }, cancellationToken).ConfigureAwait(false);

            var queryBudget = budget!;
            var changes = reader!;
            while (true)
            {
                // One row per worker call: neither a background producer nor an unbounded materialized stream.
                var row = await runner.RunAsync<StoredDocument?>(() =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    queryBudget.Resume();
                    try
                    {
                        var read = changes.Read();
                        if (queryBudget.Spent >= store.QueryBudget)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            throw new QueryCapacityException("query execution budget exceeded");
                        }

                        // ChangesAsync rows are (id, version, body, history offset).
                        var document = !read ? null : query is not null
                            ? ReadDocument(query.Collection, changes.GetString(0))
                            : changes.IsDBNull(2)
                                ? new StoredDocument(changes.GetString(0), changes.GetInt64(1), ReadOnlyMemory<byte>.Empty, Scope, Deleted: true,
                                    HistoryOffset: changes.GetInt64(3))
                                : new StoredDocument(changes.GetString(0), changes.GetInt64(1), ReadShapeJson(changes, 2), Scope,
                                    HistoryOffset: changes.GetInt64(3));
                        if (queryBudget.Spent >= store.QueryBudget)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            throw new QueryCapacityException("query execution budget exceeded");
                        }

                        return document;
                    }
                    catch (SqliteException error) when (error.SqliteErrorCode == raw.SQLITE_INTERRUPT)
                    {
                        throw Exhausted(error, cancellationToken);
                    }
                    finally
                    {
                        queryBudget.Pause();
                    }
                }, cancellationToken).ConfigureAwait(false);

                if (row is null)
                {
                    yield break;
                }

                yield return row;
            }
        }
        finally
        {
            try
            {
                // Cancellation must not abandon native cleanup or release the gate before it finishes.
                await runner.RunAsync(() =>
                {
                    try
                    {
                        reader?.Dispose();
                    }
                    finally
                    {
                        try
                        {
                            if (budget is not null)
                            {
                                StopQueryBudget();
                            }
                        }
                        finally
                        {
                            session.ObserveQueries(null);
                            command?.Dispose();
                        }
                    }
                }, CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                lock (_disposalGate)
                {
                    _streaming = false;
                    _gate.Release();
                }
            }
        }
    }

    public async ValueTask<bool> CreateCollectionAsync(CollectionInfo collection, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(collection);
        return await RunAsync(() =>
        {
            ThrowIfReadOnly();
            collection = collection with { Id = collection.Id.Length == 0 ? EntityIds.New().ToString() : collection.Id };
            if (!CreateCollection(collection))
            {
                return false;
            }

            WriteMetadata(DocumentChange.Collections, collection.Name, string.Create(CultureInfo.InvariantCulture, $$"""{"created_at":{{collection.CreatedAt}},"entity_id":"{{collection.Id}}"}"""));
            return true;
        }, cancellationToken, mutation: true).ConfigureAwait(false);
    }

    // Client databases record their collections and indexes as documents of reserved collections, so creations and drops
    // replicate with their transaction and merge like document states (TS-574). A replicated transaction's own metadata
    // stays local: the imported states carry what it needs.
    private void WriteMetadata(string collection, string name, string? body)
    {
        if (!Shaped || _importedFromOffset is not null)
        {
            return;
        }

        var offset = Offset();
        var put = BodyCommand(SqliteStatements.Put, collection, name);
        put.Parameters.AddWithValue("$version", offset);
        put.Parameters.AddWithValue("$body", (object?)body ?? DBNull.Value);
        put.ExecuteNonQuery();
        if (_changes is not null)
        {
            Capture(new DocumentChange(collection, name, offset, body is null ? null : (ReadOnlyMemory<byte>?)Encoding.UTF8.GetBytes(body)));
        }
    }

    private bool CreateCollection(CollectionInfo collection)
    {
        var command = Command(SqliteStatements.CreateCollection, collection.Name);
        command.Parameters.AddWithValue("$created", collection.CreatedAt);
        command.Parameters.AddWithValue("$entityId", collection.Id.Length == 0 ? EntityIds.New().ToString() : collection.Id);
        if (command.ExecuteNonQuery() == 0)
        {
            return false;
        }

        _metadataChanged = true;
        _collectionsChanged = true;
        // Local metadata allocates immediately, so a failed allocation faults this mutation too.
        // Apply has already pinned its starting head and will allocate its shared history at commit.
        if (_importedFromOffset is null)
        {
            Offset();
        }

        return true;
    }

    public async ValueTask<CollectionInfo?> GetCollectionAsync(string name, CancellationToken cancellationToken)
    {
        return await RunAsync(() =>
        {
            using var reader = Command(SqliteStatements.GetCollection, name).ExecuteReader();
            return reader.Read() ? new CollectionInfo(reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetString(3)) : null;
        }, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<(IReadOnlyList<CollectionInfo> Collections, bool HasMore)> ListCollectionsAsync(
        string? afterName, int limit, CancellationToken cancellationToken)
    {
        return await RunAsync<(IReadOnlyList<CollectionInfo>, bool)>(() =>
        {
            var command = session.Command(afterName is null ? SqliteStatements.ListCollectionsFirst : SqliteStatements.ListCollectionsAfter, transaction);
            if (afterName is not null)
            {
                command.Parameters.AddWithValue("$after", afterName);
            }

            command.Parameters.AddWithValue("$limit", limit + 1);
            var collections = new List<CollectionInfo>();
            using var reader = command.ExecuteReader();
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!reader.Read())
                {
                    break;
                }

                if (collections.Count == limit)
                {
                    return (collections, true);
                }

                collections.Add(new CollectionInfo(reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetString(3)));
            }

            return (collections, false);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<bool> DropCollectionAsync(string name, CancellationToken cancellationToken)
    {
        return await RunAsync(() =>
        {
            ThrowIfReadOnly();
            DocumentMutation(replicated: false);
            if (store.IsSystem && _changes is not null)
            {
                // A system group replays every mutation: each removed document, without the client bounds below.
                CaptureSystemDeletions(name, name + '\0', cancellationToken);
            }
            else if (_changes is not null)
            {
                // Release every pending body in this collection before admitting uncaptured deletions.
                // The final tombstone group must fit independently of the stored ids' enumeration order.
                for (var index = 0; index < _changes.Count; index++)
                {
                    var pending = _changes[index];
                    if (pending.Collection == name)
                    {
                        _changeBytes -= pending.Json?.Length ?? 0;
                        Replace(index, pending with { Version = Offset(), Json = null });
                    }
                }

                // Include tombstones, read only ids, and stop after the first row that cannot fit.
                var ids = Command(SqliteStatements.CollectionDocumentIds, name);
                ids.Parameters.AddWithValue("$limit", IStorageTransaction.MaxChangeGroupChanges + 1);
                using var reader = ids.ExecuteReader();
                while (reader.Read())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    // Inspect the stored size before materializing an id; replacement states are checked by Capture.
                    if ((long)Encoding.UTF8.GetByteCount(name) + reader.GetInt64(1) > IStorageTransaction.MaxChangeGroupBytes)
                    {
                        throw new ChangeGroupCapacityException();
                    }

                    Capture(new DocumentChange(name, reader.GetString(0), Offset(), null));
                }
            }

            // ponytail: one transaction deletes the collection; large captured drops fail their group bounds.
            // History first: the dropped generation is found through the collection row.
            Command(SqliteStatements.DropHistory, name).ExecuteNonQuery();
            cancellationToken.ThrowIfCancellationRequested();
            Command(SqliteStatements.DropDocuments, name).ExecuteNonQuery();
            cancellationToken.ThrowIfCancellationRequested();
            if (Command(SqliteStatements.DropCollection, name).ExecuteNonQuery() == 0)
            {
                return false;
            }

            _collectionsChanged = true;

            Write(_ => { }, cancellationToken);
            WriteMetadata(DocumentChange.Collections, name, null);
            return true;
        }, cancellationToken, mutation: true).ConfigureAwait(false);
    }

    public async ValueTask<long> CommitAsync(CancellationToken cancellationToken)
    {
        long? sequence = null;
        var offset = await RunAsync(() =>
        {
            if (_faulted)
            {
                throw new InvalidOperationException("an operation of this transaction failed, so it can only be rolled back");
            }

            try
            {
                if (_importedFromOffset is { } previous && (_appliedChanges.Count > 0 || _metadataChanged))
                {
                    // One local history offset preserves the original versions of every winning import.
                    // Imported history may use the reserved tail beyond the local merge-version ceiling.
                    _offset = Math.Max(checked(previous + 1), GetOffset());
                    var observed = Command(SqliteStatements.ObserveOffset);
                    observed.Parameters.AddWithValue("$version", _offset.Value);
                    observed.ExecuteNonQuery();
                    foreach (var change in _appliedChanges)
                    {
                        AppendChange(change.Collection, change.Id, _offset.Value, change.Version,
                            change.Json is { } json ? StrictUtf8.GetString(json.Span) : null, cancellationToken);
                    }
                }

                var offset = _offset ?? GetOffset();
                if (_changes is { Count: > 0 })
                {
                    sequence = AppendChanges(_changes, cancellationToken);
                }

                cancellationToken.ThrowIfCancellationRequested();
                transaction.Commit();
                if (_collectionsChanged)
                {
                    store.ForgetCollections();
                }

                if (_adoptedIncarnation is { } adopted)
                {
                    // Before the commit wakes waiters: readers see the adopted documents with their incarnation.
                    store.AdoptIncarnation(adopted);
                }

                return offset;
            }
            catch
            {
                _faulted = true;
                throw;
            }
        }, cancellationToken).ConfigureAwait(false);

        // Imported clock progress wakes shape waiters even when every document state lost the merge.
        if (_offset is not null || _replicaChanged || (_importedFromOffset is { } previousHead && offset > previousHead))
        {
            committed?.Invoke();
        }

        if (sequence is { } appended)
        {
            changesCommitted!(appended);
        }

        return offset;
    }

    // Savepoints are lazy: SAVEPOINT runs with the write's first statement and RELEASE with the next write's, so a
    // batched write pays no extra thread hop for them.
    private bool _savepointPending;
    private bool _releasePending;

    /// <summary>Starts the savepoint one queued write of a group commit runs in. Runs no statement yet.</summary>
    internal void Savepoint()
    {
        ThrowIfReadOnly();
        _savepoint = (_changes?.Count ?? 0, _changeBytes, _writes, _writtenBytes, _offset, _replicatedMutation, _collectionsChanged);
        _replacedSinceSavepoint.Clear();
        _savepointPending = true;
        LimitsReached = false;
    }

    /// <summary>Keeps what the write since <see cref="Savepoint"/> did. Runs no statement yet.</summary>
    internal void ReleaseSavepoint()
    {
        _ = _savepoint ?? throw new InvalidOperationException("no savepoint is open");
        if (_savepointPending)
        {
            // The write ran no statement: there is nothing to release.
            _savepointPending = false;
        }
        else
        {
            _releasePending = true;
        }

        _savepoint = null;
        _replacedSinceSavepoint.Clear();
    }

    /// <summary>Undoes what the write since <see cref="Savepoint"/> did, including a failure that faulted the transaction.</summary>
    internal ValueTask RollbackToSavepointAsync()
    {
        var saved = _savepoint ?? throw new InvalidOperationException("no savepoint is open");
        if (_savepointPending)
        {
            // The write ran no statement: only its in-memory state rolls back.
            _savepointPending = false;
            Restore(saved);
            return ValueTask.CompletedTask;
        }

        return RunVoidAsync(() =>
        {
            transaction.Rollback(GroupSavepoint);
            transaction.Release(GroupSavepoint);
            Restore(saved);
        });
    }

    private void Restore(
        (int Changes, long ChangeBytes, int Writes, long WrittenBytes, long? Offset, bool? ReplicatedMutation, bool CollectionsChanged) saved)
    {
        if (_changes is not null)
        {
            for (var i = _replacedSinceSavepoint.Count - 1; i >= 0; i--)
            {
                var (index, previous) = _replacedSinceSavepoint[i];
                _changes[index] = previous;
            }

            for (var index = saved.Changes; index < _changes.Count; index++)
            {
                _changeIndex.Remove((_changes[index].Collection, _changes[index].Id));
            }

            _changes.RemoveRange(saved.Changes, _changes.Count - saved.Changes);
        }

        _replacedSinceSavepoint.Clear();

        (_changeBytes, _writes, _writtenBytes, _offset, _replicatedMutation, _collectionsChanged) =
            (saved.ChangeBytes, saved.Writes, saved.WrittenBytes, saved.Offset, saved.ReplicatedMutation, saved.CollectionsChanged);
        _savepoint = null;
        _faulted = false;
    }

    // Worker-only: runs the savepoint statements a batched write deferred, before its next statement.
    private void RunPendingSavepoint()
    {
        if (_releasePending)
        {
            transaction.Release(GroupSavepoint);
            _releasePending = false;
        }

        if (_savepointPending)
        {
            transaction.Save(GroupSavepoint);
            _savepointPending = false;
        }
    }

    private const string GroupSavepoint = "batched";

    private async ValueTask RunVoidAsync(Action call) => await RunAsync(() =>
    {
        call();
        return true;
    }, CancellationToken.None).ConfigureAwait(false);

    public ValueTask<ReadOnlyMemory<byte>> GetOriginAsync(CancellationToken cancellationToken) =>
        RunAsync<ReadOnlyMemory<byte>>(() => GetOrigin(), cancellationToken, retry: true);

    private byte[] GetOrigin() => Ulid.Parse((string)Command(SqliteStatements.Origin).ExecuteScalar()!).ToByteArray();

    public ValueTask<long> GetLastChangeSequenceAsync(CancellationToken cancellationToken) =>
        RunAsync(LastSequence, cancellationToken, retry: true);

    // The system journal may hold its head digest without the group (a truncated tail, a follower's snapshot).
    private long LastSequence() => (long)Command(store.IsSystem ? SqliteStatements.SystemLastSequence : SqliteStatements.LastSequence).ExecuteScalar()!;

    public async ValueTask<IReadOnlyList<DocumentChange>?> ReadChangesAsync(long sequence, CancellationToken cancellationToken)
    {
        var changes = await RunAsync(() =>
        {
            var command = Command(SqliteStatements.ReadChanges);
            command.Parameters.AddWithValue("$sequence", sequence);
            command.Parameters.AddWithValue("$limit", IStorageTransaction.MaxChangeGroupChanges + 1);
            command.Parameters.AddWithValue("$maxBytes", IStorageTransaction.MaxChangeGroupBytes);
            return ReadChanges(command);
        }, cancellationToken, retry: true).ConfigureAwait(false);
        return changes.Count == 0 ? null : changes;
    }

    public ValueTask<ReadOnlyMemory<byte>?> GetChangeDigestAsync(long sequence, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sequence);
        return RunAsync(() => GetChangeDigest(sequence), cancellationToken, retry: true);
    }

    private ReadOnlyMemory<byte>? GetChangeDigest(long sequence)
    {
        if (sequence == 0)
        {
            return JournalDigest.Initial(GetOrigin());
        }

        var command = Command(SqliteStatements.GetChangeDigest);
        command.Parameters.AddWithValue("$sequence", sequence);
        return command.ExecuteScalar() is byte[] digest
            ? digest.Length == JournalDigest.Length ? digest : throw new InvalidDataException("the stored journal digest is invalid")
            : null;
    }

    public ValueTask<bool> ApplyAsync(DocumentChange change, CancellationToken cancellationToken) =>
        RunAsync(() => Apply(change, cancellationToken), cancellationToken, mutation: true);

    private bool Apply(DocumentChange change, CancellationToken cancellationToken)
    {
        ThrowIfReadOnly();
        DocumentMutation(replicated: true);
        ArgumentNullException.ThrowIfNull(change);
        if (Shaped && !DocumentChange.HasValidKey(change.Collection, change.Id))
        {
            throw new InvalidDataException("a replicated change has an invalid collection or id");
        }

        _importedFromOffset ??= _offset is { } metadataOffset ? metadataOffset - 1 : GetOffset();
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
            // Own the bytes so the stored state and its deferred history cannot diverge.
            change = change with { Json = change.Json is { } body ? body.ToArray() : (ReadOnlyMemory<byte>?)null };
        }

        _importedCount++;
        _importedBytes += bytes;
        // Advance even for a losing state: a subsequent local transaction must supersede what was observed.
        var offset = Command(SqliteStatements.ObserveOffset);
        offset.Parameters.AddWithValue("$version", change.Version);
        offset.ExecuteNonQuery();

        var retained = Command(SqliteStatements.RetainedDeletion, change.Collection);
        retained.Parameters.AddWithValue("$id", change.Id);
        retained.Parameters.AddWithValue("$version", change.Version);
        retained.Parameters.AddWithValue("$inclusive", change.Json is not null);
        // Retained deletions fence older states and equal live values. An equal tombstone can
        // restore a row lost after its ACK without replacing newer deletion evidence.
        if (retained.ExecuteScalar() is not null)
        {
            return false;
        }

        if (Shaped && change.Collection is DocumentChange.Collections or DocumentChange.Indexes)
        {
            return ApplyMetadata(change, admittedBody, cancellationToken);
        }

        if (Shaped && Dropped(change))
        {
            return false;
        }

        var changed = ApplyState(change, admittedBody);
        if (changed && WinningStateImpliesCollection(change))
        {
            CreateCollection(new CollectionInfo(change.Collection, 0));
            if (Shaped)
            {
                _appliedChanges.Add(change);
            }
        }

        return changed;
    }

    // A winning state implies its collection, whether or not its creation replicated; a losing one changes
    // nothing. A dropped collection's tombstone restores only its state: appending shape history
    // would create a new collection generation again at commit.
    private bool WinningStateImpliesCollection(DocumentChange change) =>
        !Shaped || change.Json is not null || !Dropped(change, inclusive: true)
            || Command(SqliteStatements.GetCollection, change.Collection).ExecuteScalar() is not null;

    private bool Dropped(DocumentChange change, bool inclusive = false)
    {
        var dropped = Command(SqliteStatements.DroppedCollection, change.Collection);
        dropped.Parameters.AddWithValue("$version", change.Version);
        dropped.Parameters.AddWithValue("$inclusive", inclusive);
        return dropped.ExecuteScalar() is not null;
    }

    private bool ApplyState(DocumentChange change, string? admittedBody)
    {
        if (change.Json is not null)
        {
            var document = BodyCommand(SqliteStatements.ApplyDocument, change.Collection, change.Id);
            document.Parameters.AddWithValue("$version", change.Version);
            document.Parameters.AddWithValue("$body", admittedBody!);
            return document.ExecuteNonQuery() > 0;
        }

        var deletion = Command(SqliteStatements.ApplyTombstone, change.Collection);
        deletion.Parameters.AddWithValue("$id", change.Id);
        deletion.Parameters.AddWithValue("$version", change.Version);
        return deletion.ExecuteNonQuery() > 0;
    }

    // Metadata merges like a document: only a winning state creates or drops the collection or index it describes.
    private bool ApplyMetadata(DocumentChange change, string? body, CancellationToken cancellationToken)
    {
        var collections = change.Collection == DocumentChange.Collections;
        if (collections ? !DatabaseName.TryParse(change.Id, out _) : !IndexName().IsMatch(change.Id))
        {
            throw new InvalidDataException("replicated metadata names an invalid collection or index");
        }

        var created = body is null ? null : ReadMetadata(change.Id, body, collections, cancellationToken);
        if (!ApplyState(change, body))
        {
            return false;
        }

        switch (created)
        {
            case CollectionInfo collection:
                _metadataChanged = true;
                _collectionsChanged = true;
                CreateCollection(collection);
                break;
            case IndexDefinition index:
                DropIndex(index.Name, cancellationToken);
                CreateIndex(index, cancellationToken);
                break;
            case null when collections:
                DropReplicatedCollection(change.Id, change.Version, cancellationToken);
                break;
            default:
                DropIndex(change.Id, cancellationToken);
                break;
        }

        return true;
    }

    // The documents WriteMetadata records; anything else fails the replicated group.
    private object ReadMetadata(string name, string body, bool collection, CancellationToken cancellationToken)
    {
        try
        {
            using var reservation = ReserveSchemaMemory(8L * Encoding.UTF8.GetByteCount(body), MemoryLane.Replication);
            using var json = JsonDocument.Parse(body);
            var root = json.RootElement;
            if (collection)
            {
                if (root.TryGetProperty("schema", out var schema)
                    && (!root.TryGetProperty("schema_dialect", out var dialect) || dialect.GetString() != CollectionSchemaValidation.Dialect
                        || CollectionSchemaValidation.Check(schema, store.SchemaMemory, MemoryLane.Replication, cancellationToken: cancellationToken) is not null))
                {
                    throw new InvalidDataException("replicated metadata contains an invalid collection schema");
                }

                var id = root.GetProperty("entity_id").GetString();
                return EntityIds.IsValid(id)
                    ? new CollectionInfo(name, root.GetProperty("created_at").GetInt64(), Id: id)
                    : throw new InvalidDataException("invalid collection entity id");
            }

            var fields = root.GetProperty("fields").EnumerateArray()
                .Select(field => DocumentField.TryParse(field.GetString(), out var parsed) ? parsed : throw new InvalidDataException("invalid index field"))
                .ToList();
            return DatabaseName.TryParse(root.GetProperty("collection").GetString(), out var indexed) && fields.Count is >= 1 and <= 4
                ? new IndexDefinition(name, indexed.Value, fields)
                : throw new InvalidDataException("invalid index definition");
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new InvalidDataException("replicated metadata is not a collection or index definition", error);
        }
    }

    // A replicated drop ends every live state of the collection older than itself, as the dropping member did, and removes
    // the collection once nothing live remains; a write another member made at or after the drop keeps it.
    private void DropReplicatedCollection(string name, long version, CancellationToken cancellationToken)
    {
        var tombstones = Command(SqliteStatements.TombstoneCollection, name);
        tombstones.Parameters.AddWithValue("$version", version);
        using (var reader = tombstones.ExecuteReader())
        {
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                // ponytail: one entry per document the dropping member had not seen, bounded by replication lag, not a limit.
                _appliedChanges.Add(new DocumentChange(name, reader.GetString(0), version, null));
            }
        }

        if (Command(SqliteStatements.LiveDocument, name).ExecuteScalar() is null)
        {
            Command(SqliteStatements.DropHistory, name).ExecuteNonQuery();
            if (Command(SqliteStatements.DropCollection, name).ExecuteNonQuery() > 0)
            {
                _metadataChanged = true;
                _collectionsChanged = true;
            }
            _appliedChanges.RemoveAll(applied => applied.Collection == name);
        }
    }

    public ValueTask<long> GetAppliedSequenceAsync(ReadOnlyMemory<byte> origin, CancellationToken cancellationToken) =>
        RunAsync(() =>
        {
            var command = Command(SqliteStatements.GetApplied);
            command.Parameters.AddWithValue("$origin", origin.ToArray());
            return command.ExecuteScalar() as long? ?? 0;
        }, cancellationToken);

    public ValueTask<(long Sequence, ReadOnlyMemory<byte> Digest)> GetAppliedFrontierAsync(
        ReadOnlyMemory<byte> origin, CancellationToken cancellationToken) =>
        RunAsync<(long, ReadOnlyMemory<byte>)>(() =>
        {
            var initial = JournalDigest.Initial(origin.Span);
            var command = Command(SqliteStatements.GetAppliedFrontier);
            command.Parameters.AddWithValue("$origin", origin.ToArray());
            using var reader = command.ExecuteReader();
            if (!reader.Read() || reader.IsDBNull(1))
            {
                return (0, initial);
            }

            var sequence = reader.GetInt64(0);
            var digest = reader.GetFieldValue<byte[]>(1);
            return sequence > 0 && digest.Length == JournalDigest.Length
                ? (sequence, digest)
                : throw new InvalidDataException("the stored applied frontier is invalid");
        }, cancellationToken);

    public ValueTask SetAppliedSequenceAsync(ReadOnlyMemory<byte> origin, long sequence, CancellationToken cancellationToken) =>
        SetAppliedAsync(origin, sequence, digest: null, cancellationToken);

    public ValueTask SetAppliedFrontierAsync(
        ReadOnlyMemory<byte> origin, long sequence, ReadOnlyMemory<byte> digest, CancellationToken cancellationToken) =>
        SetAppliedAsync(origin, sequence, digest, cancellationToken);

    private async ValueTask SetAppliedAsync(
        ReadOnlyMemory<byte> origin, long sequence, ReadOnlyMemory<byte>? digest, CancellationToken cancellationToken)
    {
        await RunAsync(() =>
        {
            ThrowIfReadOnly();
            if (origin.Length != 16 || sequence <= 0 || (digest is { } proof && proof.Length != JournalDigest.Length))
            {
                throw new InvalidDataException("an applied frontier requires a valid origin, sequence and digest");
            }

            var command = Command(SqliteStatements.SetApplied);
            command.Parameters.AddWithValue("$origin", origin.ToArray());
            command.Parameters.AddWithValue("$sequence", sequence);
            command.Parameters.AddWithValue("$digest", digest is { } bytes ? bytes.ToArray() : DBNull.Value);
            return command.ExecuteNonQuery();
        }, cancellationToken, mutation: true).ConfigureAwait(false);
    }

    /// <summary>Whether the change log covers every document and tombstone of this database.</summary>
    internal async Task<bool> IsCompleteAsync(CancellationToken cancellationToken) =>
        await RunAsync(() => (long)Command(SqliteStatements.Complete).ExecuteScalar()! != 0, cancellationToken).ConfigureAwait(false);

    internal async Task MarkCompleteAsync(CancellationToken cancellationToken) =>
        await RunAsync(() =>
        {
            ThrowIfReadOnly();
            return Command(SqliteStatements.MarkComplete).ExecuteNonQuery();
        }, cancellationToken, mutation: true).ConfigureAwait(false);

    internal async Task<int> CountOversizedBaselineKeysAsync(CancellationToken cancellationToken) =>
        await RunAsync(() => checked((int)(long)Command(SqliteStatements.CountOversizedBaselineKeys).ExecuteScalar()!), cancellationToken).ConfigureAwait(false);

    /// <summary>Appends one bounded baseline chunk; inadmissible pre-existing states are skipped and counted.</summary>
    internal async Task<BaselineChunk?> AppendBaselineAsync(bool tombstones, (string Collection, string Id)? after, CancellationToken cancellationToken) =>
        await RunAsync(() =>
        {
            ThrowIfReadOnly();
            return AppendBaselineChunk(tombstones, after, cancellationToken);
        }, cancellationToken, mutation: true).ConfigureAwait(false);

    private BaselineChunk? AppendBaselineChunk(bool tombstones, (string Collection, string Id)? after, CancellationToken cancellationToken)
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
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
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

        var sequence = group.Count == 0 ? 0 : AppendChanges(group, cancellationToken);
        return new BaselineChunk(key, sequence, skipped);
    }

    public ValueTask DisposeAsync()
    {
        // Every caller awaits the same cleanup, including its failure; the session returns only once.
        lock (_disposalGate)
        {
            if (_streaming)
            {
                throw new InvalidOperationException("dispose the active changes stream before using the transaction");
            }
            return new(_disposal ??= DisposeCoreAsync());
        }
    }

    private async Task DisposeCoreAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await runner.RunAsync(() =>
            {
                try
                {
                    try
                    {
                        foreach (var command in _bodyCommands.Values)
                        {
                            command.Parameters.Clear();
                            command.Dispose();
                        }

                        _bodyCommands.Clear();
                        _changes?.Clear();
                        _changeIndex.Clear();
                        _replacedSinceSavepoint.Clear();
                        _appliedChanges.Clear();
                    }
                    finally
                    {
                        transaction.Dispose();
                    }

                    if (!write)
                    {
                        // Failed read-only reset closes the session instead of reusing it.
                        using var writable = session.Connection.CreateCommand();
                        writable.CommandText = "PRAGMA query_only = OFF;";
                        writable.ExecuteNonQuery();
                    }

                    if (durable)
                    {
                        // Likewise a failed reset: pooled sessions keep decision 0003's NORMAL.
                        using var normal = session.Connection.CreateCommand();
                        normal.CommandText = "PRAGMA synchronous = NORMAL;";
                        normal.ExecuteNonQuery();
                    }
                }
                catch
                {
                    store.Discard(session);
                    throw;
                }

                // Still admitted until pooled or closed, including an eviction checkpoint.
                store.Return(session);
            }, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            store.End(write, ownsAdmission);
            _gate.Release();
        }
    }

    private long GetOffset() => (long?)session.Command(SqliteStatements.CurrentOffset, transaction).ExecuteScalar() ?? 0;

    // Every write in one transaction shares one offset, allocated under the database writer gate.
    private long Offset()
    {
        if (_offset is null)
        {
            VersionClock.EnsureCanAllocate(GetOffset());
        }

        _offset ??= session.Command(SqliteStatements.NextOffset, transaction).ExecuteScalar() is long offset
            ? offset
            : throw new OverflowException("the commit offset is exhausted");
        return _offset.Value;
    }

    // Worker-only, inside a mutation: a refused write also refuses the commit.
    private void ThrowIfReadOnly()
    {
        if (!write)
        {
            throw new InvalidOperationException("a read transaction cannot mutate database state");
        }

        if (_faulted)
        {
            throw new InvalidOperationException("an operation of this transaction failed, so it can only be rolled back");
        }
    }

    // Check calls before no-op detection too, independently of whether this database captures changes.
    private void DocumentMutation(bool replicated)
    {
        if (_replicatedMutation is { } previous && previous != replicated)
        {
            throw new InvalidOperationException("a transaction cannot mix local document mutations and replicated changes");
        }

        _replicatedMutation = replicated;
    }

    private Task EnterAsync(CancellationToken cancellationToken, bool stream = false, bool mutation = false)
    {
        lock (_disposalGate)
        {
            ObjectDisposedException.ThrowIf(_disposal is not null, this);
            if (_streaming)
            {
                if (mutation)
                {
                    // Mark refusal before stream cleanup can admit a concurrent commit.
                    _faulted = true;
                }
                throw new InvalidOperationException("dispose the active changes stream before using the transaction");
            }
            _streaming = stream;
            // Admission and waiting are atomic with disposal, so cleanup queues after every accepted call.
            return _gate.WaitAsync(cancellationToken);
        }
    }

    private async Task EnterStreamAsync(CancellationToken cancellationToken)
    {
        // Reserve the stream before waiting: later calls must not queue behind a suspended consumer.
        var waiting = EnterAsync(cancellationToken, stream: true);
        try
        {
            await waiting.ConfigureAwait(false);
        }
        catch
        {
            lock (_disposalGate)
            {
                _streaming = false;
            }
            throw;
        }
    }

    // retry: journal reads use the session's cancellable busy handling (SqliteSession.ExecuteAsync).
    private ValueTask<T> RunAsync<T>(Func<T> call, CancellationToken cancellationToken, bool mutation = false, bool retry = false) =>
        // A submitted mutation keeps its ordered gate slot even if cancelled: poison before a later commit.
        RunCoreAsync(EnterAsync(mutation ? CancellationToken.None : cancellationToken, mutation: mutation), call, mutation, retry, cancellationToken);

    private async ValueTask<T> RunCoreAsync<T>(Task waiting, Func<T> call, bool mutation, bool retry, CancellationToken cancellationToken)
    {
        await waiting.ConfigureAwait(false);
        try
        {
            if (retry)
            {
                return await session.ExecuteAsync(runner, () =>
                {
                    RunPendingSavepoint();
                    return call();
                }, cancellationToken).ConfigureAwait(false);
            }

            return await runner.RunAsync(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                RunPendingSavepoint();
                return call();
            }, cancellationToken).ConfigureAwait(false);
        }
        catch when (mutation)
        {
            // Includes pre-cancellation and queue cancellation before the worker callback runs.
            _faulted = true;
            throw;
        }
        finally
        {
            _gate.Release();
        }
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

    // Worker-only: reads one stored change group within the journal limits.
    internal static List<DocumentChange> ReadChanges(SqliteCommand command)
    {
        var changes = new List<DocumentChange>();
        long bytes = 0;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            bytes += reader.GetInt64(4);
            if (changes.Count >= IStorageTransaction.MaxChangeGroupChanges || bytes > IStorageTransaction.MaxChangeGroupBytes)
            {
                throw new InvalidDataException("the stored change group exceeds the journal limits");
            }

            changes.Add(Change(reader));
        }

        return changes;
    }

    // Capacity errors occur during the mutation, before copying JSON, as well as at commit.
    // The mutation marks the transaction rollback-only even when its caller catches the error.
    private void CheckCaptureCapacity(long bytes, bool replacing = false)
    {
        if ((!replacing && _changes!.Count >= IStorageTransaction.MaxChangeGroupChanges)
            || bytes > IStorageTransaction.MaxChangeGroupBytes - _changeBytes)
        {
            LimitsReached = _changes!.Count > 0;
            throw new ChangeGroupCapacityException(
                $"a replicated transaction holds at most {IStorageTransaction.MaxChangeGroupChanges} changes and {IStorageTransaction.MaxChangeGroupBytes} bytes");
        }
    }

    // Replaces a captured state, remembering the old one while a batched write's savepoint may still roll it back.
    private void Replace(int index, DocumentChange state)
    {
        if (_savepoint is { } saved && index < saved.Changes)
        {
            _replacedSinceSavepoint.Add((index, _changes![index]));
        }

        _changes![index] = state;
    }

    private void Capture(DocumentChange change)
    {
        if (store.IsSystem)
        {
            CaptureSystem(change);
            return;
        }

        if (!DocumentChange.HasValidKey(change.Collection, change.Id))
        {
            throw new InvalidDataException("a captured change has an invalid collection or id");
        }

        var index = _changeIndex.GetValueOrDefault((change.Collection, change.Id), -1);
        var bytes = ContentBytes(change) - (index >= 0 ? ContentBytes(_changes![index]) : 0);
        CheckCaptureCapacity(bytes, replacing: index >= 0);
        // Reserve only the final state, before copying it; shape history still counts every mutation.
        var owned = change with { Json = change.Json is { } json ? json.ToArray() : (ReadOnlyMemory<byte>?)null };
        if (index >= 0)
        {
            Replace(index, owned);
        }
        else
        {
            _changeIndex[(change.Collection, change.Id)] = _changes!.Count;
            _changes.Add(owned);
        }

        _changeBytes += bytes;
    }

    // Appends one change group and returns its sequence. The next sequence is read inside this write
    // transaction, which the database's writer gate serializes.
    private long AppendChanges(List<DocumentChange> changes, CancellationToken cancellationToken)
    {
        if (!store.IsSystem && (changes.Count > IStorageTransaction.MaxChangeGroupChanges || _changeBytes > IStorageTransaction.MaxChangeGroupBytes))
        {
            throw new ChangeGroupCapacityException(
                $"a replicated transaction holds at most {IStorageTransaction.MaxChangeGroupChanges} changes and {IStorageTransaction.MaxChangeGroupBytes} bytes");
        }

        var sequence = store.IsSystem ? checked(LastSequence() + 1)
            : Command(SqliteStatements.NextSequence).ExecuteScalar() is long next ? next : throw new OverflowException("the change sequence is exhausted");
        var previous = GetChangeDigest(sequence - 1) ?? throw new InvalidDataException("the preceding journal digest is missing");
        byte[] digest;
        using (var group = new JournalDigest.Group(GetOrigin(), sequence, previous.Span, changes.Count))
        {
            changes.ForEach(group.Append);
            digest = group.Complete();
        }

        var ordinal = 0;
        foreach (var change in changes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var append = BodyCommand(SqliteStatements.AppendReplicationChange, change.Collection, change.Id);
            append.Parameters.AddWithValue("$sequence", sequence);
            append.Parameters.AddWithValue("$ordinal", ordinal++);
            append.Parameters.AddWithValue("$version", change.Version);
            append.Parameters.AddWithValue("$body", change.Json is { } json ? Encoding.UTF8.GetString(json.Span) : DBNull.Value);
            append.ExecuteNonQuery();
        }

        cancellationToken.ThrowIfCancellationRequested();
        var prefix = Command(SqliteStatements.AppendChangeDigest);
        prefix.Parameters.AddWithValue("$sequence", sequence);
        prefix.Parameters.AddWithValue("$digest", digest);
        prefix.ExecuteNonQuery();
        if (store.IsSystem)
        {
            TruncateSystemJournal(sequence - ISystemReplication.RetainedGroups);
        }

        return sequence;
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

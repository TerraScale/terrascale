using System.Buffers;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using SQLitePCL;
using TerraBase.Features.Memory;
using TerraBase.Features.Qos;
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

    // The system database's capture: every write, in order (docs/decisions/0009).
    private readonly List<DocumentChange>? _changes = changesCommitted is not null && store.IsSystem ? [] : null;

    // A client database's capture: one delta per document, the join of this transaction's writes to it, in first-write
    // order (docs/design/anna-core.md, Per-origin streams).
    private readonly List<CapturedDelta>? _deltas = changesCommitted is not null && !store.IsSystem ? [] : null;

    // Where each captured document's delta sits in _deltas, so a capture finds its predecessor in O(1).
    private readonly Dictionary<(string Collection, string Id), int> _changeIndex = [];

    // Captured deltas a batched write replaced since its savepoint, to put back if it rolls back.
    private readonly List<(int Index, CapturedDelta Previous)> _replacedSinceSavepoint = [];

    // Body statements live only for this transaction: idle sessions must not retain bound JSON.
    private readonly Dictionary<string, SqliteCommand> _bodyCommands = [with(StringComparer.Ordinal)];

    private bool _metadataChanged;
    private int _importedCount;
    private long _importedBytes;
    private long _changeBytes;
    private bool? _replicatedMutation;

    // A refused or failed write makes this transaction able only to roll back.
    private bool _faulted;
    private int _writes;
    private long _writtenBytes;

    // This transaction's local commit offset, separate from document versions.
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

    public ValueTask<long> GetDocumentBytesAsync(string collection, string id, CancellationToken cancellationToken) =>
        RunAsync(() =>
        {
            var command = Command(SqliteStatements.DocumentBytes, collection);
            command.Parameters.AddWithValue("$id", id);
            return command.ExecuteScalar() as long? ?? 0;
        }, cancellationToken);

    public ValueTask<long> GetStoredDocumentBytesAsync(CancellationToken cancellationToken) =>
        RunAsync(() => (long)Command(SqliteStatements.StoredDocumentBytes).ExecuteScalar()!, cancellationToken);

    // Worker-only: query readers fetch bodies after their id/key page has admitted the row.
    private StoredDocument? ReadDocument(string collection, string id)
    {
        if (Shaped)
        {
            return ReadUniqueDocument(collection, id, reserveResult: true);
        }

        var command = Command(SqliteStatements.Get, collection);
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        if (!reader.Read() || reader.IsDBNull(1))
        {
            return null;
        }

        ReserveJson(reader.GetInt64(2));
        return new StoredDocument(id, reader.GetInt64(0), reader.GetFieldValue<byte[]>(1), Scope);
    }

    public async ValueTask<DocumentVersion?> GetVersionAsync(string collection, string id, CancellationToken cancellationToken)
    {
        return await RunAsync(() => ReadVersion(collection, id), cancellationToken).ConfigureAwait(false);
    }

    // Worker-only.
    private DocumentVersion? ReadVersion(string collection, string id)
    {
        var command = Command(UniqueVersion, collection);
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        return reader.Read() ? new DocumentVersion(reader.GetInt64(0), reader.GetBoolean(1)) : null;
    }

    // Worker-only; runs inside a mutation, so a rejected document also makes the transaction roll back only.
    private string Admit(ReadOnlyMemory<byte> json) =>
        // The private system catalog also stores scalar indexes; tenant documents must be objects.
        DocumentJson.Read(json, requireObject: store.Tenant is not null);

    public async ValueTask<long> PutAsync(string collection, string id, ReadOnlyMemory<byte> json, CancellationToken cancellationToken)
    {
        return await RunAsync(() => Put(collection, id, json, cancellationToken), cancellationToken, mutation: true).ConfigureAwait(false);
    }

    // One call for the whole run: each put otherwise costs two hops through the write lane and back.
    public async ValueTask PutBatchAsync(
        IReadOnlyList<(string Collection, string Id, ReadOnlyMemory<byte> Json)> puts, Memory<(long Version, DocumentVersion? Previous)> results,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(puts);
        ArgumentOutOfRangeException.ThrowIfLessThan(results.Length, puts.Count);
        results.Span[..puts.Count].Clear();
        await RunAsync(() =>
        {
            for (var i = 0; i < puts.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var (collection, id, json) = puts[i];
                var previous = ReadVersion(collection, id);
                results.Span[i] = (Put(collection, id, json, cancellationToken), previous);
            }

            return true;
        }, cancellationToken, mutation: true).ConfigureAwait(false);
    }

    // Worker-only, inside a mutation.
    private long Put(string collection, string id, ReadOnlyMemory<byte> json, CancellationToken cancellationToken)
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
        if (store.IsSystem)
        {
            return Write(offset =>
            {
                if (_changes is not null)
                {
                    CaptureSystem(new DocumentChange(collection, id, offset, json));
                }

                var put = BodyCommand(SqliteStatements.Put, collection, id);
                put.Parameters.AddWithValue("$version", offset);
                put.Parameters.AddWithValue("$body", body);
                put.ExecuteNonQuery();
            }, cancellationToken);
        }

        var affected = ReplaceUniqueKeys(collection, id, body, cancellationToken);
        ClearUniqueConflict(collection, id);
        cancellationToken.ThrowIfCancellationRequested();
        var offset = Offset();
        var version = WriteRegister(collection, id, json.Span, tombstone: false);
        ReconcileUnique(collection, affected, id, cancellationToken);
        AppendChange(collection, id, offset, version, body, cancellationToken);
        return version;
    }

    public async ValueTask<bool> DeleteAsync(string collection, string id, CancellationToken cancellationToken)
    {
        return await RunAsync(() =>
        {
            ThrowIfReadOnly();
            DocumentMutation(replicated: false);
            if (!store.IsSystem)
            {
                // A register removed without a trace would come back with the next late delta: deletions are tombstones.
                throw new NotSupportedException("a client database deletes documents with tombstones");
            }

            var command = Command(SqliteStatements.Delete, collection);
            command.Parameters.AddWithValue("$id", id);
            if (command.ExecuteNonQuery() == 0)
            {
                return false;
            }

            // Catalog followers still need the deletion.
            if (_changes is not null)
            {
                Write(offset => CaptureSystem(new DocumentChange(collection, id, offset, null)), cancellationToken);
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
            var version = Command(UniqueVersion, collection);
            version.Parameters.AddWithValue("$id", id);
            using (var reader = version.ExecuteReader())
            {
                if (!reader.Read() || reader.GetBoolean(1))
                {
                    return null;
                }
            }

            Reserve(collection, id, 0);
            // ponytail: tombstones stay until tombstone GC on applied vectors (TS-647 step 7).
            if (!store.IsSystem)
            {
                var affected = ReplaceUniqueKeys(collection, id, null, cancellationToken);
                ClearUniqueConflict(collection, id);
                cancellationToken.ThrowIfCancellationRequested();
                var offset = Offset();
                var written = WriteRegister(collection, id, default, tombstone: true);
                ReconcileUnique(collection, affected, id, cancellationToken);
                AppendChange(collection, id, offset, written, null, cancellationToken);
                if (Command(DroppedCollection, collection).ExecuteScalar() is not null)
                {
                    // A concurrent drop won the collection while a write it never saw kept it alive: deleting that last
                    // document completes the drop here, as applying this tombstone does on every other member.
                    DropReplicatedCollection(collection, cancellationToken);
                }

                return written;
            }

            return Write(offset =>
            {
                if (_changes is not null)
                {
                    CaptureSystem(new DocumentChange(collection, id, offset, null));
                }

                var command = Command(SqliteStatements.Tombstone, collection);
                command.Parameters.AddWithValue("$id", id);
                command.Parameters.AddWithValue("$version", offset);
                command.ExecuteNonQuery();
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

        // An implicit collection carries the same captured identity metadata as an explicit creation.
        cancellationToken.ThrowIfCancellationRequested();
        CreateCollection(new CollectionInfo(collection, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
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

        if (_deltas is not null && !DocumentDelta.HasValidKey(collection, id))
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
                ReserveJson(size);
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
            if (budget.Exceeded)
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

    private bool ScansLimited => !store.IsSystem && QosContext.Current is { } current && QosPolicy.For(current.Plan).ScanRows != long.MaxValue;

    private const int MaxSnapshotSteps = 10_000;
    private const int MaxSnapshotSkips = 50_000;

    // The handler and its removal run only on workers while this transaction owns the connection.
    private QueryBudget StartQueryBudget(CancellationToken cancellationToken)
    {
        var budget = new QueryBudget(store.Time, store.QueryBudget,
            !store.IsSystem && QosContext.Current is { } tenant ? QosPolicy.For(tenant.Plan) : null);
        raw.sqlite3_progress_handler(session.Connection.Handle, 1000,
            _ => cancellationToken.IsCancellationRequested || budget.Progress() ? 1 : 0, null);
        if (budget.ScansLimited)
        {
            // octet_length reads the stored byte size without decoding or copying the JSON body.
            session.Connection.CreateFunction<long, long>("tb_qos_scan", size =>
            {
                if (budget.Scan(size))
                {
                    raw.sqlite3_interrupt(session.Connection.Handle);
                    return 0;
                }

                return 1;
            });
        }

        return budget;
    }

    private void StopQueryBudget()
    {
        raw.sqlite3_progress_handler(session.Connection.Handle, 0, null, null);
        session.Connection.CreateFunction<long, long>("tb_qos_scan", null);
    }

    private static QueryCapacityException Exhausted(SqliteException error, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return new QueryCapacityException("query execution budget exceeded", error);
    }

    // A stream accumulates SQLite execution time, excluding worker queueing and consumer time between rows.
    private sealed class QueryBudget(TimeProvider time, TimeSpan defaultDeadline, QosPolicy? policy)
    {
        private bool _running = true;
        private long _started = time.GetTimestamp();
        private TimeSpan _paused;

        private long _instructions;
        private long _rows;
        private long _bytes;
        private bool ScanExceeded { get; set; }

        public bool ScansLimited => policy is not null && (policy.ScanRows != long.MaxValue || policy.ScanBytes != long.MaxValue);

        public bool Exceeded => ScanExceeded || _instructions >= (policy?.QuerySteps ?? 5_000_000)
            || (policy?.QueryTimeout ?? defaultDeadline) is var deadline && deadline != Timeout.InfiniteTimeSpan && Spent >= deadline;

        public bool Progress()
        {
            _instructions += 1000;
            return Exceeded;
        }

        public bool Scan(long size)
        {
            if (_rows >= policy!.ScanRows || size > policy.ScanBytes - _bytes)
            {
                ScanExceeded = true;
                return true;
            }

            _rows++;
            _bytes += size;
            return false;
        }

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
        command.CommandText = new SqliteQueryCompiler(command.Parameters, ScansLimited).Compile(query, observation is not null);
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
            // ReadDocument admits the body's detached copies before the body and sort keys allocate.
            var document = ReadDocument(query.Collection, reader.GetString(0))!;
            var keys = new object?[reader.FieldCount - 3];
            for (var i = 0; i < keys.Length; i++)
            {
                // Read string key bytes with SQLite's length, preserving NULs in continuation keys.
                keys[i] = reader.IsDBNull(3 + i) ? null
                    : i % 2 == 1 && reader.GetInt64(2 + i) == 5
                        ? Encoding.UTF8.GetString(reader.GetFieldValue<byte[]>(3 + i))
                        : reader.GetValue(3 + i);
            }

            rows.Add(new QueryRow(document, keys));
        }

        return new QueryPage(rows, HasMore: false);
    }

    public async ValueTask<(long Count, bool Complete)> CountAsync(
        string collection, DocumentFilter? filter, long maxExamined, CancellationToken cancellationToken, QueryObservation? observation = null)
    {
        return await RunAsync(() => WithQueryBudget(() =>
        {
            using var command = Uncached(collection);
            command.CommandText = new SqliteQueryCompiler(command.Parameters, ScansLimited).CompileCount(filter, maxExamined, observation is not null);
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
            command.CommandText = new SqliteQueryCompiler(command.Parameters, ScansLimited).CompileSnapshot(query.Filter, generation, offset);
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

                    if (budget.Exceeded)
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
                    rows.Add(new QueryRow(new StoredDocument(id, reader.GetInt64(2), ReadShapeJson(reader, 3, reader.GetInt64(4)), Scope), []));
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
            command.CommandText = new SqliteQueryCompiler(command.Parameters, ScansLimited).CompileChanges(filter, after, through);
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
                using var interrupt = cancellationToken.Register(() => raw.sqlite3_interrupt(session.Connection.Handle));
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
                    using var interrupt = cancellationToken.Register(() => raw.sqlite3_interrupt(session.Connection.Handle));
                    try
                    {
                        var read = changes.Read();
                        if (queryBudget.Exceeded)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            throw new QueryCapacityException("query execution budget exceeded");
                        }

                        if (read && query is null && changes.IsDBNull(2))
                        {
                            ReserveJson(0);
                        }

                        // ChangesAsync rows are (id, version, body, history offset).
                        var document = !read ? null : query is not null
                            ? ReadDocument(query.Collection, changes.GetString(0))
                            : changes.IsDBNull(2)
                                ? new StoredDocument(changes.GetString(0), changes.GetInt64(1), ReadOnlyMemory<byte>.Empty, Scope, Deleted: true,
                                    HistoryOffset: changes.GetInt64(3))
                                : new StoredDocument(changes.GetString(0), changes.GetInt64(1), ReadShapeJson(changes, 2, changes.GetInt64(4)), Scope,
                                    HistoryOffset: changes.GetInt64(3));
                        if (queryBudget.Exceeded)
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
            return CreateCollection(collection);
        }, cancellationToken, mutation: true).ConfigureAwait(false);
    }

    // Client databases record their collections and indexes as documents of reserved collections, so creations and drops
    // replicate with their transaction and join like documents (TS-574). A replicated transaction's own metadata stays
    // local: the applied registers carry what it needs.
    private void WriteMetadata(string collection, string name, string? body)
    {
        if (!Shaped || _imported)
        {
            return;
        }

        Offset();
        WriteRegister(collection, name, body is null ? default : Encoding.UTF8.GetBytes(body), tombstone: body is null);
    }

    private bool CreateCollection(CollectionInfo collection)
    {
        collection = collection with { Id = collection.Id.Length == 0 ? EntityIds.New().ToString() : collection.Id };
        if (!EntityIds.IsValid(collection.Id))
        {
            throw new ArgumentException("a collection id must be a canonical uppercase ULID", nameof(collection));
        }

        var command = Command(SqliteStatements.CreateCollection, collection.Name);
        command.Parameters.AddWithValue("$created", collection.CreatedAt);
        command.Parameters.AddWithValue("$entityId", collection.Id);
        if (command.ExecuteNonQuery() == 0)
        {
            return false;
        }

        _metadataChanged = true;
        _collectionsChanged = true;
        // Local metadata allocates immediately, so a failed allocation faults this mutation too. Applied changes allocate
        // their shared history offset at commit.
        if (!_imported)
        {
            Offset();
        }

        WriteMetadata(DocumentDelta.Collections, collection.Name, string.Create(CultureInfo.InvariantCulture, $$"""{"created_at":{{collection.CreatedAt}},"entity_id":"{{collection.Id}}"}"""));
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

    public ValueTask<bool> HasCollectionStateAsync(string name, bool prefix, CancellationToken cancellationToken) =>
        RunAsync(() =>
        {
            ArgumentException.ThrowIfNullOrEmpty(name);
            var through = prefix ? PrefixSuccessor(name) : name + '\0';
            var command = session.Command((through is null, store.IsSystem) switch
            {
                (true, true) => SqliteStatements.HasCollectionStateFrom,
                (false, true) => SqliteStatements.HasCollectionState,
                (true, false) => SqliteStatements.ClientHasCollectionStateFrom,
                (false, false) => SqliteStatements.ClientHasCollectionState,
            }, transaction);
            command.Parameters.AddWithValue("$from", name);
            if (through is not null)
            {
                command.Parameters.AddWithValue("$to", through);
            }
            return (long)command.ExecuteScalar()! != 0;
        }, cancellationToken);

    private static string? PrefixSuccessor(string name)
    {
        var length = name.Length;
        while (length > 0)
        {
            if (Rune.DecodeLastFromUtf16(name.AsSpan(0, length), out var rune, out var consumed) != OperationStatus.Done)
            {
                throw new ArgumentException("Collection prefixes must contain valid Unicode scalar values.", nameof(name));
            }

            length -= consumed;
            if (rune.Value < 0x10ffff)
            {
                var next = rune.Value == 0xd7ff ? 0xe000 : rune.Value + 1;
                return name[..length] + new Rune(next).ToString();
            }
        }

        return null;
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
            if (store.IsSystem)
            {
                if (_changes is not null)
                {
                    // A system group replays every mutation: each removed document.
                    CaptureSystemDeletions(name, name + '\0', cancellationToken);
                }

                Command(SqliteStatements.DropHistory, name).ExecuteNonQuery();
                cancellationToken.ThrowIfCancellationRequested();
                Command(SqliteStatements.DropDocuments, name).ExecuteNonQuery();
            }
            else if (Command(SqliteStatements.GetCollection, name).ExecuteScalar() is not null)
            {
                // A tombstone over every live document, which replicates with the drop; the registers stay, so a late
                // delta of one of them joins its tombstone instead of bringing it back. Their history goes with the
                // generation below.
                // ponytail: one transaction drops the collection; one of more than 1024 live documents fails its group bounds.
                var live = Command(SqliteStatements.LiveIds, name);
                live.Parameters.AddWithValue("$limit", IStorageTransaction.MaxChangeGroupChanges + 1);
                var ids = new List<string>();
                using (var reader = live.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        ids.Add(reader.GetString(0));
                    }
                }

                // A retained uniqueness loser is live in its register, though no reader sees it.
                var hidden = Command(UniqueHiddenIds, name);
                hidden.Parameters.AddWithValue("$limit", IStorageTransaction.MaxChangeGroupChanges + 1);
                using (var reader = hidden.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        ids.Add(reader.GetString(0));
                    }
                }

                if (ids.Count > IStorageTransaction.MaxChangeGroupChanges)
                {
                    LimitsReached = _deltas?.Count > 0;
                    throw new ChangeGroupCapacityException(
                        $"a replicated transaction holds at most {IStorageTransaction.MaxChangeGroupChanges} changes and {IStorageTransaction.MaxChangeGroupBytes} bytes");
                }

                // Documents this transaction already captured first: their tombstones replace their bodies, so the group
                // has room for the others whatever order their ids sort in.
                foreach (var id in ids.OrderBy(id => _changeIndex.ContainsKey((name, id)) ? 0 : 1))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    WriteRegister(name, id, default, tombstone: true);
                }

                // History after the tombstones, before the collection row it finds the dropped generation through.
                Command(SqliteStatements.DropHistory, name).ExecuteNonQuery();
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (Command(SqliteStatements.DropCollection, name).ExecuteNonQuery() == 0)
            {
                return false;
            }

            _collectionsChanged = true;

            DropUniqueCollection(name);

            Write(_ => { }, cancellationToken);
            WriteMetadata(DocumentDelta.Collections, name, null);
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
                if (_imported && (_importHistory || _metadataChanged))
                {
                    // One local history offset for every applied write a reader can see; their versions stay theirs.
                    CommitImportHistory(Offset());
                }

                DropUniqueAffected();
                var offset = _offset ?? GetOffset();
                if (_changes is { Count: > 0 })
                {
                    sequence = AppendChanges(_changes, cancellationToken);
                }
                else if (_deltas is { Count: > 0 })
                {
                    sequence = AppendGroup(_deltas, cancellationToken);
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

        if (_offset is not null || _replicaChanged)
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
        _savepoint = (_changes?.Count ?? _deltas?.Count ?? 0, _changeBytes, _writes, _writtenBytes, _offset, _replicatedMutation, _collectionsChanged);
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
        _changes?.RemoveRange(saved.Changes, _changes.Count - saved.Changes);
        if (_deltas is not null)
        {
            for (var i = _replacedSinceSavepoint.Count - 1; i >= 0; i--)
            {
                var (index, previous) = _replacedSinceSavepoint[i];
                _deltas[index] = previous;
            }

            for (var index = saved.Changes; index < _deltas.Count; index++)
            {
                _changeIndex.Remove((_deltas[index].Collection, _deltas[index].Id));
            }

            _deltas.RemoveRange(saved.Changes, _deltas.Count - saved.Changes);
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

    // The system database's origin is its incarnation; a client database's is its own stream's (ClientOrigin).
    private byte[] GetOrigin() =>
        store.IsSystem ? Ulid.Parse((string)Command(SqliteStatements.Origin).ExecuteScalar()!).ToByteArray() : ClientOrigin();

    public ValueTask<long> GetLastChangeSequenceAsync(CancellationToken cancellationToken) =>
        RunAsync(LastSequence, cancellationToken, retry: true);

    // The system journal may hold its head digest without the group (a truncated tail, a follower's snapshot).
    private long LastSequence() => (long)Command(store.IsSystem ? SqliteStatements.SystemLastSequence : SqliteStatements.LastGroup).ExecuteScalar()!;

    public ValueTask<CapturedGroup?> ReadChangesAsync(long sequence, CancellationToken cancellationToken) =>
        store.IsSystem
            ? throw new NotSupportedException("the system database's groups are read through its catalog replication")
            : RunAsync(() => ReadGroup(sequence), cancellationToken, retry: true);

    /// <summary>See <see cref="ISystemReplication.GetDigestAsync"/>.</summary>
    internal ValueTask<ReadOnlyMemory<byte>?> GetChangeDigestAsync(long sequence, CancellationToken cancellationToken)
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

    public ValueTask<bool> ApplyAsync(DocumentDelta change, CancellationToken cancellationToken) =>
        RunAsync(() =>
        {
            ThrowIfReadOnly();
            DocumentMutation(replicated: true);
            return ApplyDelta(change, cancellationToken);
        }, cancellationToken, mutation: true);

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

    public ValueTask<long> GetAppliedSequenceAsync(ReadOnlyMemory<byte> origin, CancellationToken cancellationToken) =>
        RunAsync(() =>
        {
            var command = Command(SqliteStatements.GetApplied);
            command.Parameters.AddWithValue("$origin", origin.ToArray());
            return command.ExecuteScalar() as long? ?? 0;
        }, cancellationToken);

    public async ValueTask SetAppliedSequenceAsync(ReadOnlyMemory<byte> origin, long sequence, CancellationToken cancellationToken) =>
        await RunAsync(() =>
        {
            ThrowIfReadOnly();
            if (store.IsSystem || origin.Length != OriginPositions.OriginBytes || sequence <= 0)
            {
                throw new InvalidDataException("an applied position needs a client database, a 16-byte origin and a sequence");
            }

            var command = Command(SqliteStatements.SetApplied);
            command.Parameters.AddWithValue("$origin", origin.ToArray());
            command.Parameters.AddWithValue("$sequence", sequence);
            command.ExecuteNonQuery();
            var superseded = Command(SqliteStatements.DropSupersededApplied);
            superseded.Parameters.AddWithValue("$origin", origin.ToArray());
            return superseded.ExecuteNonQuery();
        }, cancellationToken, mutation: true).ConfigureAwait(false);

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
                        _deltas?.Clear();
                        _changeIndex.Clear();
                        _replacedSinceSavepoint.Clear();
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
                // Interrupting a mutation rolls back SQLite's entire transaction, including other batch savepoints.
                // Standalone transactions can interrupt native calls; grouped writes cancel between statements.
                using var interrupt = write && !ownsAdmission ? default : cancellationToken.Register(() => raw.sqlite3_interrupt(session.Connection.Handle));
                var result = call();
                if (mutation)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                return result;
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException error) when (error.SqliteErrorCode == raw.SQLITE_INTERRUPT && cancellationToken.IsCancellationRequested)
        {
            if (mutation)
            {
                _faulted = true;
            }

            throw new OperationCanceledException("the storage operation was cancelled", error, cancellationToken);
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

    // Native octet_length projections inspect size without copying JSON; GetBytes/GetStream would allocate first.
    private void ReserveJson(long size)
    {
        QosContext.ReserveResult(checked(size * 3 + 2048), store.IsSystem ? null : QosContext.Current);
    }

    private byte[] ReadShapeJson(SqliteDataReader reader, int ordinal, long size)
    {
        ReserveJson(size);
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

    // Capacity errors occur during the mutation, as well as at commit. The mutation marks the transaction rollback-only
    // even when its caller catches the error. A client group of one change may be larger (a baseline's register).
    private void CheckCaptureCapacity(long bytes, bool replacing = false)
    {
        var count = _deltas!.Count;
        if ((!replacing && count >= IStorageTransaction.MaxChangeGroupChanges)
            || bytes > IStorageTransaction.MaxChangeGroupBytes - _changeBytes)
        {
            LimitsReached = count > 0;
            throw new ChangeGroupCapacityException(
                $"a replicated transaction holds at most {IStorageTransaction.MaxChangeGroupChanges} changes and {IStorageTransaction.MaxChangeGroupBytes} bytes");
        }
    }

    // Appends one system journal group and returns its sequence. The next sequence is read inside this write transaction,
    // which the database's writer gate serializes.
    private long AppendChanges(List<DocumentChange> changes, CancellationToken cancellationToken)
    {
        var sequence = checked(LastSequence() + 1);
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
        TruncateSystemJournal(sequence - ISystemReplication.RetainedGroups);
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


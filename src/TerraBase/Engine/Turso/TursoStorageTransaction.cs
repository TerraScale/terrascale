using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using Turso.Data.Sqlite;
using TerraBase.Engine.Lattices;
using TerraBase.Features.Qos;
using TerraBase.Primitives;

namespace TerraBase.Engine.Turso;

/// <summary>
/// One transaction; every Turso call runs on its runner: the <see cref="TursoIoPool"/> for reads, the database's
/// <see cref="TursoWriteLane"/> for writes.
/// </summary>
/// <param name="store">The database this transaction belongs to.</param>
/// <param name="session">The rented session; returned to <paramref name="store"/> on dispose.</param>
/// <param name="transaction">The Turso transaction.</param>
/// <param name="runner">Runs every Turso call of this transaction.</param>
/// <param name="write">Whether mutations are allowed.</param>
/// <param name="committed">Runs after a commit that changed the database.</param>
/// <param name="ownsAdmission">Whether disposal releases this transaction's I/O admission.</param>
internal sealed partial class TursoStorageTransaction(
    TursoDatabaseStore store, TursoSession session, SqliteTransaction transaction, TursoRunner runner, bool write, Action? committed,
    bool ownsAdmission)
    : IStorageTransaction
{
    internal TursoDatabaseStore.CopyOperation? Operation { get; init; }

    // Bounds one commit's written bytes, every repeated write and its framing included.
    private const long MaxWrittenBytes = 7 * 1024 * 1024;
    private const int MaxDocumentBytes = DocumentJson.MaxBytes;
    private const int MaxWrites = 1024;
    private const int MaxIdBytes = 1024;
    private const int OpFramingBytes = 128;
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    // Serializes calls and disposal, so two workers never use the same connection concurrently.
    private readonly QosContext.ResultMemory? _stateReadMemory =
        store.SchemaMemory is { } memory ? new QosContext.ResultMemory(memory) : null;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Lock _disposalGate = new();
    private Task? _disposal;
    private bool _streaming;

    private long _work;

    public long Work => Volatile.Read(ref _work);

    private long _changeBytes;

    // A refused or failed write makes this transaction able only to roll back.
    private bool _faulted;

    // Set once COMMIT returned: a later failure resetting the pooled session no longer makes the write look failed.
    private bool _committed;
    private bool _collected;
    private int _writes;
    private long _writtenBytes;

    // This transaction's local commit offset, separate from document versions.
    private long? _offset;

    // Set by an index build, whose ANALYZE only connections opened after its commit plan with.
    private bool _statisticsChanged;

    // Group commit (TursoDatabaseStore.WriteAsync): each queued write runs inside a savepoint, and this snapshot of the
    // in-memory capture and limits rolls back with it.
    private (long ChangeBytes, int Captured, int Writes, long WrittenBytes, long? Offset, bool CollectionsChanged)?
        _savepoint;

    /// <summary>Whether a per-commit limit refused a write since the last savepoint: alone in a transaction, it could fit.</summary>
    internal bool LimitsReached { get; private set; }

    /// <summary>The offset this transaction committed at, set as soon as its Turso commit succeeded; null before.</summary>
    internal CommitResult? Committed { get; private set; }

    /// <summary>
    /// Whether this transaction created or dropped a collection, state readers see beside its documents: the store forgets
    /// its collection names on commit.
    /// </summary>
    internal bool ChangedReaderState { get; private set; }

    public Guid Scope { get; } = store.Scope;

    public ValueTask<StoredDocument?> GetAsync(string collection, string id, CancellationToken cancellationToken) =>
        RunAsync(() => ReadDocument(collection, id), cancellationToken);

    public ValueTask<long> GetDocumentBytesAsync(string collection, string id, CancellationToken cancellationToken) =>
        RunAsync(() =>
        {
            if (store.IsSystem)
            {
                var command = Command(TursoStatements.DocumentBytes, collection);
                command.Parameters.AddWithValue("$id", id);
                CountRead("point");
                return command.ExecuteScalar() as long? ?? 0;
            }

            if (!HasTable(collection))
            {
                return 0;
            }

            CountRead("point");
            return TableCommand(TursoStatements.ClientDocumentBytes, collection, id).ExecuteScalar() as long? ?? 0;
        }, cancellationToken);

    public ValueTask<long> GetStoredDocumentBytesAsync(CancellationToken cancellationToken) =>
        RunAsync(() =>
        {
            if (store.IsSystem)
            {
                return (long)Command(TursoStatements.StoredDocumentBytes).ExecuteScalar()!;
            }

            // Hidden uniqueness losers are kept aside, out of their collections' tables.
            return (long)Command(TursoStatements.ClientConflictBytes).ExecuteScalar()!
                + (long)Command(TursoStatements.ClientStoredBytes).ExecuteScalar()!;
        }, cancellationToken);

    // Worker-only: query readers fetch bodies after their id/key page has admitted the row.
    private StoredDocument? ReadDocument(string collection, string id, string path = "point")
    {
        if (IsClient)
        {
            return ReadUniqueDocument(collection, id, reserveResult: true, path: path);
        }

        var command = Command(TursoStatements.Get, collection);
        command.Parameters.AddWithValue("$id", id);
        CountRead(path);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        ReserveJson(reader.GetInt64(2));
        return new StoredDocument(id, Version(collection, id, reader, 0), SystemValue(TursoSession.Bytes(reader, 1)), Scope);
    }

    public async ValueTask<DocumentVersion?> GetVersionAsync(string collection, string id, CancellationToken cancellationToken)
    {
        return await RunAsync(() => ReadVersion(collection, id), cancellationToken).ConfigureAwait(false);
    }

    // Worker-only.
    private DocumentVersion? ReadVersion(string collection, string id)
    {
        if (!HasTable(collection))
        {
            return null;
        }

        var command = store.IsSystem ? Command(TursoStatements.Version, collection) : TableCommand(UniqueVersion, collection);
        command.Parameters.AddWithValue("$id", id);
        CountRead("point");
        using var reader = command.ExecuteReader();
        if (!reader.Read() || (reader.IsDBNull(0) && !_pending.ContainsKey((collection, id))))
        {
            return null;
        }

        return new DocumentVersion(Version(collection, id, reader, 0), reader.GetBoolean(1));
    }

    // Worker-only; runs inside a mutation, so a rejected document also makes the transaction roll back only. The private
    // system catalog also stores scalars (name indexes, sequences): a document state holds objects only, so a system
    // scalar is stored as the object {"\u0000": value} and read back as the value (SystemValue). A system object that
    // holds that field itself is refused. An admitted client document (PutAdmittedAsync) was read by its caller already.
    private ReadOnlyMemory<byte> Admit(ReadOnlyMemory<byte> json, bool admitted)
    {
        if (admitted && !store.IsSystem)
        {
            return json;
        }

        var body = DocumentJson.Read(json, requireObject: store.Tenant is not null);
        if (!store.IsSystem)
        {
            return json;
        }

        using var parsed = JsonDocument.Parse(json, DocumentState.JsonOptions);
        return parsed.RootElement.ValueKind != JsonValueKind.Object ? Encoding.UTF8.GetBytes($"{{{ScalarField}{body}}}")
            : parsed.RootElement.TryGetProperty("\0", out _) ? throw new InvalidDataException("a system document field is named \\u0000")
            : json;
    }

    private const string ScalarField = "\"\\u0000\":";
    private static readonly byte[] ScalarPrefix = Encoding.UTF8.GetBytes("{" + ScalarField);

    // A stored system document, as it was written: a scalar comes back without its wrapper (see Admit).
    private static byte[] SystemValue(byte[] body) =>
        body.AsSpan().StartsWith(ScalarPrefix) ? body[ScalarPrefix.Length..^1] : body;

    public async ValueTask<long> PutAsync(string collection, string id, ReadOnlyMemory<byte> json, CancellationToken cancellationToken)
    {
        return await RunAsync(() => Put(collection, id, json, cancellationToken).Version, cancellationToken, mutation: true).ConfigureAwait(false);
    }

    public async ValueTask<(long Version, ReadOnlyMemory<byte> Stored)> PutAdmittedAsync(
        string collection, string id, ReadOnlyMemory<byte> json, CancellationToken cancellationToken)
    {
        return await RunAsync<(long, ReadOnlyMemory<byte>)>(() =>
        {
            var (version, state) = Put(collection, id, json, cancellationToken, admitted: true);
            return (version, state.Materialize());
        }, cancellationToken, mutation: true).ConfigureAwait(false);
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
                results.Span[i] = (Put(collection, id, json, cancellationToken).Version, previous);
            }

            return true;
        }, cancellationToken, mutation: true).ConfigureAwait(false);
    }

    public async ValueTask<long> PatchAsync(
        string collection, string id, ReadOnlyMemory<byte> json, IReadOnlyList<FieldOperation> operations, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operations);
        return await RunAsync(() => Put(collection, id, json, cancellationToken, operations).Version, cancellationToken, mutation: true).ConfigureAwait(false);
    }

    // Worker-only, inside a mutation. A PATCH writes `patch` to a client document's state; `json` is what it leaves.
    private (long Version, DocumentState State) Put(
        string collection, string id, ReadOnlyMemory<byte> json, CancellationToken cancellationToken, IReadOnlyList<FieldOperation>? patch = null,
        bool admitted = false)
    {
        ThrowIfReadOnly();
        var body = Admit(json, admitted);
        long? metadataBytes = null;
        using var schemaReservation = IsClient ? ReserveCollectionMetadata(collection, out metadataBytes) : null;
        var exists = HasTable(collection);
        if (IsClient && exists && metadataBytes is { } bytes)
        {
            ReserveJson(bytes);
        }

        // Schema admission and the drop barrier share the same collection state and JSON lookup.
        var metadata = IsClient && exists ? CollectionState(collection) : DocumentState.Bottom;
        if (exists && metadataBytes is not null && metadata.Equals(DocumentState.Bottom))
        {
            throw new InvalidDataException("collection metadata has no stored state");
        }

        if (IsClient && ReadCollectionSchema(metadata) is { } schema)
        {
            CollectionSchemaValidation.Validate(schema, json, cancellationToken, store.SchemaMemory, store.SchemaEvaluations);
        }

        Reserve(id, json.Length);
        if (!exists)
        {
            // An implicit collection carries the same captured identity metadata as an explicit creation.
            CreateCollection(new CollectionInfo(collection, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()), cancellationToken);
            if (IsClient)
            {
                metadata = CollectionState(collection);
            }
        }

        var (state, at) = BeginDocument(collection, id, metadata.Clear is { Deleted: true } drop ? drop : null);
        DocumentState next;
        if (patch is not null)
        {
            next = patch.Aggregate(state, (current, operation) => operation.Apply(current, at));
        }
        else
        {
            using var parsed = JsonDocument.Parse(body, DocumentState.JsonOptions);
            next = state.Put(at, parsed.RootElement);
        }

        // Reads and uniqueness see the materialized document: canonical JSON, fields in name order.
        var materialized = Materialize(next);
        var affected = ReplaceUniqueKeys(collection, id, materialized, cancellationToken);
        ClearUniqueConflict(collection, id);
        cancellationToken.ThrowIfCancellationRequested();
        Offset();
        var version = Store(collection, id, next, at, materialized, state, metadata);
        ReconcileUnique(collection, affected, cancellationToken);
        return (version, next);
    }

    // A register removed without a trace would come back with the next late delta: every deletion is a tombstone.
    public async ValueTask<bool> DeleteAsync(string collection, string id, CancellationToken cancellationToken) =>
        await TombstoneAsync(collection, id, cancellationToken).ConfigureAwait(false) is not null;

    public async ValueTask<long?> TombstoneAsync(string collection, string id, CancellationToken cancellationToken)
    {
        return await RunAsync<long?>(() =>
        {
            ThrowIfReadOnly();
            // Check liveness from metadata without reading a body, within this serialized call.
            if (ReadVersion(collection, id) is not { Deleted: false })
            {
                return null;
            }

            Reserve(id, 0);
            // ponytail: tombstones stay until tombstone GC on applied vectors (TS-647 step 7).
            var affected = ReplaceUniqueKeys(collection, id, null, cancellationToken);
            ClearUniqueConflict(collection, id);
            cancellationToken.ThrowIfCancellationRequested();
            Offset();
            var metadata = IsClient ? CollectionState(collection) : DocumentState.Bottom;
            var (state, at) = BeginDocument(collection, id, metadata.Clear is { Deleted: true } drop ? drop : null);
            var written = Store(collection, id, state.Delete(at), at, null, state, metadata);
            ReconcileUnique(collection, affected, cancellationToken);
            return written;
        }, cancellationToken, mutation: true).ConfigureAwait(false);
    }

    // Client commits keep per-commit limits and metadata documents; _system is never a client database.
    private DocumentState CollectionState(string name) =>
        _pending.TryGetValue((DocumentDelta.Collections, name), out var pending) ? pending.State : ReadState(DocumentDelta.Collections, name, localWrite: true);

    private ClearEntry? CollectionDrop(string name) => CollectionState(name).Clear is { Deleted: true } drop ? drop : null;

    // Local writes keep the collection clear and mint above it even when observing a remote clock was capped.
    private (DocumentState State, EntryVersion At) BeginDocument(string collection, string id, ClearEntry? collectionDrop)
    {
        var (state, at) = Begin(collection, id);
        if (collectionDrop is { } drop)
        {
            state = state.Join(new DocumentState(drop, DocumentState.Bottom.Fields));
            if (at.Stamp.Value <= drop.Version.Stamp.Value)
            {
                at = at with { Stamp = Member.Clock.Tick(drop.Version.Stamp) };
            }
        }

        return (state, at);
    }

    private bool IsClient => !store.IsSystem;

    private long Write(Action<long> apply, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var offset = Offset();
        apply(offset);
        return offset;
    }

    // Admits one write against the per-commit limits of a client database.
    private void Reserve(string id, int documentBytes)
    {
        if (!IsClient)
        {
            return;
        }

        // An empty id has no successor to resume after.
        if (id.Length == 0)
        {
            throw new InvalidDataException("a document id is empty");
        }

        var bytes = documentBytes + (long)JsonEncodedText.Encode(id).EncodedUtf8Bytes.Length + OpFramingBytes;
        if (documentBytes > MaxDocumentBytes || Encoding.UTF8.GetByteCount(id) > MaxIdBytes || _writes == MaxWrites
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
            if (!HasTable(collection))
            {
                return new DocumentPage([], HasMore: false);
            }

            var command = store.IsSystem
                ? Command(afterId is null ? TursoStatements.ListFirst : TursoStatements.ListAfter, collection)
                : TableCommand(afterId is null ? TursoStatements.ClientListFirst : TursoStatements.ClientListAfter, collection);
            if (afterId is not null)
            {
                command.Parameters.AddWithValue("$after", afterId);
            }

            // One extra row tells whether another page exists without reading its body.
            command.Parameters.AddWithValue("$limit", limit + 1);
            var documents = new List<StoredDocument>();
            long bytes = 0;
            CountRead("query");
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
                var id = reader.GetString(0);
                var body = TursoSession.Bytes(reader, 2);
                documents.Add(new StoredDocument(id, Version(collection, id, reader, 1), store.IsSystem ? SystemValue(body) : body, Scope));
            }

            return new DocumentPage(documents, HasMore: false);
        }, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<QueryPage> QueryAsync(DocumentQuery query, CancellationToken cancellationToken, QueryObservation? observation = null)
    {
        ArgumentNullException.ThrowIfNull(query);
        return await RunAsync(() => WithQueryBudget(() => ReadQuery(query, observation, cancellationToken),
            cancellationToken, observation), cancellationToken).ConfigureAwait(false);
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
        catch (SqliteException error) when (TursoSession.Stopped(error))
        {
            throw Exhausted(error, cancellationToken);
        }
        finally
        {
            StopQueryBudget();
            session.ObserveQueries(null);
        }
    }

    private QueryBudget? _queryBudget;

    // The handler and its removal run only on workers while this transaction owns the connection.
    private QueryBudget StartQueryBudget(CancellationToken cancellationToken)
    {
        var budget = new QueryBudget(store.Time, store.QueryBudget,
            !store.IsSystem && QosContext.Current is { } tenant ? QosPolicy.For(tenant.Plan) : null,
            store.SchemaMemory is { } memory ? new QosContext.ResultMemory(memory) : null);
        _queryBudget = budget;
        session.Connection.SetFtsProgressHandler((work, bytes) =>
            cancellationToken.IsCancellationRequested || budget.Native(work, bytes));
        // SQL row functions and native FTS share this request's work and deadline.
        // The clock is read every 32nd row, about as often as SQLite's handler ran (every 1000 instructions).
        var rows = 0;
        session.StopWhen(() => cancellationToken.IsCancellationRequested || (++rows % 32 == 0 && budget.Exceeded));
        session.Charge(size => budget.Limited && budget.Scan(size), () => budget.Limited && budget.Row());
        return _queryBudget = budget;
    }

    private void StopQueryBudget()
    {
        session.Connection.SetFtsProgressHandler(null);
        _queryBudget?.Dispose();
        _queryBudget = null;
        session.StopWhen(null);
        session.Charge(null, null);
    }

    private static QueryCapacityException Exhausted(SqliteException error, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return new QueryCapacityException("query execution budget exceeded", error);
    }

    // A stream accumulates Turso execution time, excluding worker queueing and consumer time between rows.
    private sealed class QueryBudget(TimeProvider time, TimeSpan defaultDeadline, QosPolicy? policy, QosContext.ResultMemory? memory) : IDisposable
    {
        private bool _running = true;
        private long _started = time.GetTimestamp();
        private TimeSpan _paused;

        // Turso counts no VM instructions, so a candidate row charges about what SQLite ran for it: 8 for the row
        // and 4 per JSON read. This keeps the plans' former instruction limits (1M Hobby, 5M Pro) at their scale.
        private const long RowWork = 8;
        private const long ReadWork = 4;

        private readonly long _workLimit = policy?.QueryWork ?? 5_000_000;
        private long _rows;
        private long _bytes;
        private long _nativeBytes;
        private long _nativeReserved;
        private bool ScanExceeded { get; set; }

        private int _jsonReads;

        /// <summary>Charges each later candidate row the JSON reads (filter and sort keys) of the statement about to run.</summary>
        public void Charge(string sql) => _jsonReads = JsonRead().Count(sql);

        /// <summary>Engine work units charged so far.</summary>
        public long Work { get; private set; }

        public bool Limited => _workLimit != long.MaxValue
            || policy is not null && (policy.ScanRows != long.MaxValue || policy.ScanBytes != long.MaxValue);

        // Execution time, engine work and scanned candidates bound a statement.
        public bool Exceeded => ScanExceeded
            || (policy?.QueryTimeout ?? defaultDeadline) is var deadline && deadline != Timeout.InfiniteTimeSpan && Spent >= deadline;

        /// <summary>Charges one row combination of a relational read; true once the work limit is spent.</summary>
        public bool Row()
        {
            Work += RowWork;
            ScanExceeded |= Work > _workLimit;
            return ScanExceeded;
        }

        public bool Scan(long size)
        {
            Work += RowWork + ReadWork * _jsonReads;
            if (Work > _workLimit || policy is not null && (_rows >= policy.ScanRows || size > policy.ScanBytes - _bytes))
            {
                ScanExceeded = true;
                return true;
            }

            _rows++;
            _bytes += size;
            return false;
        }

        // Native residency, temporary chunks and scorer buffers are charged before allocation, including cache hits.
        public bool Native(ulong work, ulong bytes)
        {
            var byteLimit = Math.Min(policy?.ScanBytes ?? 128 * 1024 * 1024, 128 * 1024 * 1024);
            if (Exceeded || work > (ulong)Math.Max(0, _workLimit - Work)
                || bytes > (ulong)Math.Max(0, byteLimit - _nativeBytes))
            {
                ScanExceeded = true;
                return true;
            }

            Work += (long)work;
            _nativeBytes += (long)bytes;
            if (memory is not null && _nativeBytes > _nativeReserved)
            {
                var reserve = Math.Min(64 * 1024, byteLimit - _nativeReserved);
                reserve = Math.Max(reserve, _nativeBytes - _nativeReserved);
                try
                {
                    memory.Reserve(reserve, QosContext.Current);
                    _nativeReserved += reserve;
                }
                catch (QosMemoryException)
                {
                    ScanExceeded = true;
                    return true;
                }
            }

            return false;
        }

        public void Dispose() => memory?.Dispose();

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

    // Bound both candidate count and JSON bytes before Turso retains bodies in its temporary sorter.
    private void CheckSortBudget(string collection, long maxBytes, CancellationToken cancellationToken,
        QueryObservation? observation = null, string? candidates = null, DocumentFilter? filter = null)
    {
        using var command = Uncached(collection);
        // Owner admission covers the entire readable set, before submitted predicates/candidates.
        command.CommandText = new TursoQueryCompiler(command.Parameters, Rows(collection),
            candidates: filter is AuthorizedFilter ? null : candidates)
            .CompileSortAdmission(filter, observation is not null);
        CountRead("query");
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
        using var searches = PrepareSearch(query.Collection, query.Filter, cancellationToken);
        var page = ReadQuery(query, searches, observation, cancellationToken);
        return query.Explain ? page with { Plan = ExplainQuery(query, searches, observation, cancellationToken) } : page;
    }

    // The plan of the statement the query just ran, with what running it cost: EXPLAIN QUERY PLAN prepares, never runs.
    private DocumentQueryPlan ExplainQuery(DocumentQuery query, SearchResults searches, QueryObservation? observation, CancellationToken cancellationToken)
    {
        using var command = Uncached(query.Collection);
        var compiler = new TursoQueryCompiler(command.Parameters, Rows(query.Collection), searches.Ordinals, searches.Candidates);
        command.CommandText = "EXPLAIN QUERY PLAN " + (query.Aggregate is null ? compiler.Compile(query) : compiler.CompileAggregate(query));
        var steps = new List<string>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                steps.Add(reader.GetString(3));
            }
        }

        var indexes = steps.SelectMany(step => UsedIndex().Matches(step).Select(match => match.Groups[1].Value)).Distinct().ToList();
        return new DocumentQueryPlan(steps, indexes, observation?.RowsScanned ?? 0, _queryBudget?.Work ?? 0);
    }

    private QueryPage ReadQuery(DocumentQuery query, SearchResults searches, QueryObservation? observation, CancellationToken cancellationToken)
    {
        if (query.Aggregate is not null)
        {
            using var grouped = Uncached(query.Collection);
            grouped.CommandText = new TursoQueryCompiler(grouped.Parameters, Rows(query.Collection), searches.Ordinals, searches.Candidates)
                .CompileAggregate(query, observation is not null);
            _queryBudget!.Charge(grouped.CommandText);
            return ReadGroups(query, grouped, cancellationToken);
        }

        if (query.Sort.Count > 0 && (!query.Sort[0].Path.IsId || query.Sort[0].Score))
        {
            CheckSortBudget(query.Collection, query.MaxBytes, cancellationToken, observation, searches.Candidates, query.Filter);
        }

        // Compiled per request so every new filter shape does not grow the session's command cache.
        using var command = Uncached(query.Collection);
        // A vector index narrows a near query to the candidates it reranks exactly.
        var candidates = searches.Candidates ?? VectorCandidates(query, cancellationToken);
        command.CommandText = new TursoQueryCompiler(command.Parameters, Rows(query.Collection), searches.Ordinals, candidates).Compile(query, observation is not null);
        _queryBudget!.Charge(command.CommandText);
        if (query.Near is not { } near)
        {
            return ReadRows(query, command, cancellationToken);
        }

        session.Near(new NearDistance(near).Measure);
        try
        {
            return ReadRows(query, command, cancellationToken);
        }
        finally
        {
            session.Near(null);
        }
    }

    private QueryPage ReadGroups(DocumentQuery query, SqliteCommand command, CancellationToken cancellationToken)
    {
        var aggregate = query.Aggregate!;
        var keys = aggregate.GroupBy.Count;
        var groups = new List<QueryGroup>();
        CountRead("query");
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (groups.Count == query.Limit)
            {
                throw new QueryValidationException($"the aggregate has more than {query.Limit} groups: narrow the filter or raise limit (at most 1000)");
            }

            var key = new (long Rank, object? Value)[keys];
            for (var i = 0; i < keys; i++)
            {
                key[i] = (reader.GetInt64(2 * i), reader.IsDBNull((2 * i) + 1) ? null : reader.GetValue((2 * i) + 1));
            }

            var values = new double?[aggregate.Values.Count];
            for (var i = 0; i < values.Length; i++)
            {
                values[i] = reader.IsDBNull((2 * keys) + 1 + i) ? null : reader.GetDouble((2 * keys) + 1 + i);
            }

            groups.Add(new QueryGroup(key, reader.GetInt64(2 * keys), values));
        }

        return new QueryPage([], HasMore: false, groups);
    }

    private QueryPage ReadRows(DocumentQuery query, SqliteCommand command, CancellationToken cancellationToken)
    {
        var rows = new List<QueryRow>();
        long bytes = 0;
        CountRead("query");
        using var reader = command.ExecuteReader();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!reader.Read())
            {
                break;
            }

            // Documents without a comparable vector sort last, at +Infinity.
            if (query.Near is not null && double.IsPositiveInfinity(reader.GetDouble(3)))
            {
                break;
            }

            var size = reader.GetInt64(2);
            if (rows.Count == query.Limit || (rows.Count > 0 && bytes + size > query.MaxBytes))
            {
                // A near query answers its nearest documents in one page.
                return new QueryPage(rows, HasMore: query.Near is null);
            }

            bytes += size;
            // ReadDocument admits the body's detached copies before the body and sort keys allocate.
            var document = ReadDocument(query.Collection, reader.GetString(0), "query")!;
            var keys = new object?[reader.FieldCount - 3];
            for (var i = 0; i < keys.Length; i++)
            {
                // Read string key bytes with Turso's length, preserving NULs in continuation keys.
                keys[i] = reader.IsDBNull(3 + i) ? null
                    : i % 2 == 1 && reader.GetInt64(2 + i) == 5
                        ? Encoding.UTF8.GetString(TursoSession.Bytes(reader, 3 + i))
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
            // A client count examines the collection's state rows: this transaction's writes need theirs.
            FlushStates();
            using var searches = PrepareSearch(collection, filter, cancellationToken);
            using var command = Uncached(collection);
            command.CommandText = new TursoQueryCompiler(command.Parameters, Rows(collection), searches.Ordinals, searches.Candidates).CompileCount(filter, maxExamined, observation is not null);
            _queryBudget!.Charge(command.CommandText);
            CountRead("query");
            using var reader = command.ExecuteReader();
            cancellationToken.ThrowIfCancellationRequested();
            reader.Read();
            return (reader.GetInt64(1), reader.GetInt64(0) <= maxExamined);
        }, cancellationToken, observation), cancellationToken).ConfigureAwait(false);
    }

    public ValueTask<long> GetOffsetAsync(CancellationToken cancellationToken) =>
        RunAsync(() => (long?)session.Command(TursoStatements.CurrentOffset, transaction).ExecuteScalar() ?? 0, cancellationToken);

    private async IAsyncEnumerable<StoredDocument> ReadBudgetedAsync(
        Func<SqliteCommand> createCommand, SqlQuery query, QueryObservation? observation,
        [EnumeratorCancellation] CancellationToken cancellationToken)
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
                    if (query.Order.Count > 0
                        && ReadSqlPlan(query, cancellationToken).Steps.Any(step => step.Contains("USE SORTER", StringComparison.Ordinal)))
                    {
                        CheckSortBudget(query.Collection, 8 * 1024 * 1024, cancellationToken, observation);
                    }

                    budget.Charge(command.CommandText);
                    CountRead("query");
                    reader = command.ExecuteReader();
                }
                catch (SqliteException error) when (TursoSession.Stopped(error))
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
                        if (queryBudget.Exceeded)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            throw new QueryCapacityException("query execution budget exceeded");
                        }

                        var document = read ? ReadDocument(query.Collection, changes.GetString(0), "query") : null;
                        if (queryBudget.Exceeded)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            throw new QueryCapacityException("query execution budget exceeded");
                        }

                        return document;
                    }
                    catch (SqliteException error) when (TursoSession.Stopped(error))
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
                        // Reset before finalizing: native finalization drains rows and invokes the cancelled scan guard.
                        reader?.Abort();
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
            return CreateCollection(collection, cancellationToken);
        }, cancellationToken, mutation: true).ConfigureAwait(false);
    }

    // Client databases record their collections and indexes as documents of reserved collections, so creations and drops
    // replicate with their transaction and join like documents (TS-574). A replicated transaction's own metadata stays
    // local: the applied registers carry what it needs.
    private void WriteMetadata(string collection, string name, string? body)
    {
        if (!IsClient)
        {
            return;
        }

        Offset();
        var (state, at) = Begin(collection, name);
        if (collection == DocumentDelta.Collections && _pending.ContainsKey((collection, name)))
        {
            // A drop must follow this transaction's row writes, and recreation must follow its drop. Keep that
            // drop in the final delta too: Changed selects captured entries without changing their merge version.
            var previous = at.Stamp;
            at = at with { Stamp = Member.Clock.Tick(previous) };
            if (state.Clear is { } clear && clear.Changed == previous)
            {
                state = new DocumentState(clear with { Changed = at.Stamp }, state.Fields);
            }
        }

        using var parsed = body is null ? null : JsonDocument.Parse(body, DocumentState.JsonOptions);
        var target = collection == DocumentDelta.Unique ? name
            : collection == DocumentDelta.Indexes && parsed is not null
                && parsed.RootElement.TryGetProperty("collection", out var named) ? named.GetString() : null;
        if (target is not null && CollectionDrop(target) is { } drop)
        {
            if (at.Stamp.Value <= drop.Version.Stamp.Value)
            {
                at = at with { Stamp = Member.Clock.Tick(drop.Version.Stamp) };
            }
        }

        if (parsed is null)
        {
            Store(collection, name, state.Delete(at), at, null);
            return;
        }

        DocumentState next;
        if (collection == DocumentDelta.Collections)
        {
            // Collection clears only record drops; a concurrent creation must not replace the deletion barrier.
            next = parsed.RootElement.EnumerateObject().Aggregate(state, (current, property) => current.Set(at, property.Name, property.Value));
            foreach (var field in state.Fields.Keys.Where(field => !parsed.RootElement.TryGetProperty(field, out _)))
            {
                next = next.Unset(at, field);
            }
        }
        else
        {
            next = state.Put(at, parsed.RootElement);
        }

        Store(collection, name, next, at, Materialize(next));
    }

    private bool CreateCollection(CollectionInfo collection, CancellationToken cancellationToken)
    {
        collection = collection with { Id = collection.Id.Length == 0 ? EntityIds.New().ToString() : collection.Id };
        if (!EntityIds.IsValid(collection.Id))
        {
            throw new ArgumentException("a collection id must be a canonical uppercase ULID", nameof(collection));
        }

        if (CollectionName.IsReserved(collection.Name))
        {
            throw new ArgumentException("the collection name is reserved", nameof(collection));
        }

        var command = Command(TursoStatements.CreateCollection, collection.Name);
        command.Parameters.AddWithValue("$created", collection.CreatedAt);
        command.Parameters.AddWithValue("$entityId", collection.Id);
        if (command.ExecuteNonQuery() == 0)
        {
            return false;
        }

        if (!store.IsSystem)
        {
            using var table = PlainCommand(CreateTable(collection.Name));
            table.ExecuteNonQuery();
        }

        ChangedReaderState = true;
        // Local metadata allocates immediately, so a failed allocation faults this mutation too.
        Offset();

        WriteMetadata(DocumentDelta.Collections, collection.Name, string.Create(CultureInfo.InvariantCulture, $$"""{"created_at":{{collection.CreatedAt}},"entity_id":"{{collection.Id}}"}"""));
        if (IsClient && _pending[(DocumentDelta.Collections, collection.Name)].State.Clear is { Deleted: true } drop)
        {
            RestoreDroppedCollectionIndexes(collection.Name, drop, cancellationToken);
        }

        return true;
    }

    public async ValueTask<CollectionInfo?> GetCollectionAsync(string name, CancellationToken cancellationToken)
    {
        return await RunAsync(() =>
        {
            using var reader = Command(TursoStatements.GetCollection, name).ExecuteReader();
            return reader.Read() ? new CollectionInfo(reader.GetString(0), reader.GetInt64(1), reader.GetString(2)) : null;
        }, cancellationToken).ConfigureAwait(false);
    }

    public ValueTask<bool> HasCollectionStateAsync(string name, bool prefix, CancellationToken cancellationToken) =>
        RunAsync(() =>
        {
            ArgumentException.ThrowIfNullOrEmpty(name);
            var through = prefix ? PrefixSuccessor(name) : name + '\0';
            var command = session.Command(through is null ? TursoStatements.HasCollectionStateFrom : TursoStatements.HasCollectionState, transaction);
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
            var command = session.Command(afterName is null ? TursoStatements.ListCollectionsFirst : TursoStatements.ListCollectionsAfter, transaction);
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

                collections.Add(new CollectionInfo(reader.GetString(0), reader.GetInt64(1), reader.GetString(2)));
            }

            return (collections, false);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<bool> DropCollectionAsync(string name, CancellationToken cancellationToken)
    {
        return await RunAsync(() =>
        {
            ThrowIfReadOnly();
            if (IsClient)
            {
                if (Command(TursoStatements.GetCollection, name).ExecuteScalar() is null)
                {
                    return false;
                }

                Write(_ => { }, cancellationToken);
                WriteMetadata(DocumentDelta.Collections, name, null);
                var drop = _pending[(DocumentDelta.Collections, name)].State.Clear!.Value;
                DropCoveredCollection(name, drop, cancellationToken);
                return true;
            }

            Command(TursoStatements.DropDocuments, name).ExecuteNonQuery();
            RemoveMerkleStates(name);
            Command(TursoStatements.DropStates, name).ExecuteNonQuery();

            cancellationToken.ThrowIfCancellationRequested();
            if (DropCollectionRow(name) == 0)
            {
                return false;
            }

            ChangedReaderState = true;

            DropUniqueCollection(name, local: true);

            Write(_ => { }, cancellationToken);
            WriteMetadata(DocumentDelta.Collections, name, null);
            return true;
        }, cancellationToken, mutation: true).ConfigureAwait(false);
    }

    private void DropProgress()
    {
        if (Interlocked.Increment(ref _work) % 512 == 0 && store.Tenant is { } tenant)
        {
            store.Checkpoint?.Invoke("storage.drop.progress", tenant);
        }
    }

    // A collection clear dominates the same entries as a document clear; last-applied row stamps are not lattice versions.
    private void DropCoveredCollection(string name, ClearEntry drop, CancellationToken cancellationToken)
    {
        var clear = new DocumentState(drop, DocumentState.Bottom.Fields);
        var retained = false;
        var pendingIds = _pending.Keys.Where(key => key.Collection == name).Select(key => key.Id).ToArray();
        // Read once: the loop changes neither the definition nor the drop it is weighed against, and each read is admitted
        // for the whole transaction.
        IReadOnlyList<UniqueConstraint> constraints = IsClient ? ReadUniqueConstraints(name) : [];
        foreach (var id in CollectionDropIds(name, cancellationToken).Where(id => !_pending.ContainsKey((name, id))).Concat(pendingIds))
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Each document is written before the next is read, so its admission ends with its iteration: an unbounded
            // collection must not accumulate leases for the whole drop.
            using var admission = store.SchemaMemory is { } memory ? new QosContext.ResultMemory(memory) : null;
            var state = _pending.TryGetValue((name, id), out var pending) ? pending.State : ReadState(name, id, admission);
            var next = state.Join(clear);
            if (next.Equals(state) && !next.Equals(clear))
            {
                retained = true;
                DropProgress();
                continue;
            }

            var body = Materialize(next);
            var affected = ReplaceUniqueKeys(name, id, body, cancellationToken, local: false, constraints);
            ClearUniqueConflict(name, id);
            PutRow(name, id, body);
            _pending.Remove((name, id));
            if (next.Equals(clear))
            {
                RemoveMerkleState(name, id);
                var delete = Command("DELETE FROM _tb_state WHERE collection = $collection AND id = $id;", name);
                delete.Parameters.AddWithValue("$id", id);
                delete.ExecuteNonQuery();
            }
            else
            {
                retained = true;
                // Derived projection, not a new local operation: origin 0 prevents the CDC tailer from echoing it.
                WriteState(name, id, next, new EntryVersion(drop.Version.Stamp, default));
            }

            ReconcileUnique(name, affected, cancellationToken);
            DropProgress();
        }

        foreach (var index in ReadIndexes(cancellationToken).Where(index => index.Collection == name
            && !(ReadState(DocumentDelta.Indexes, index.Name).Clear?.Version > drop.Version)))
        {
            _ = DropIndex(index.Name, cancellationToken);
        }

        if (retained)
        {
            ReprojectUnique(name, cancellationToken);
            SetDroppedCollectionIdentity(name, drop);
        }
        else
        {
            DropTable(name);
            DropCollectionRow(name);
            DropUniqueCollection(name, local: false);
        }

        ChangedReaderState = true;
    }

    private IEnumerable<string> CollectionDropIds(string name, CancellationToken cancellationToken)
    {
        var after = string.Empty;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var command = Command("SELECT id FROM _tb_state WHERE collection = $collection AND id > $after ORDER BY id LIMIT 128;", name);
            command.Parameters.AddWithValue("$after", after);
            List<string> ids = [];
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    ids.Add(reader.GetString(0));
                }
            }

            if (ids.Count == 0)
            {
                yield break;
            }

            foreach (var id in ids)
            {
                yield return id;
            }

            after = ids[^1];
        }
    }

    private static string DroppedCollectionId(ClearEntry drop)
    {
        Span<byte> bytes = stackalloc byte[16];
        BinaryPrimitives.WriteUInt64BigEndian(bytes, drop.Version.Stamp.Value);
        BinaryPrimitives.WriteUInt64BigEndian(bytes[8..], drop.Version.Node.Value);
        return new Ulid(bytes).ToString();
    }

    private void AddDroppedCollection(string name, ClearEntry drop, CancellationToken cancellationToken)
    {
        AddCollection(name, drop.Version.Stamp.Milliseconds, DroppedCollectionId(drop));
        RestoreDroppedCollectionIndexes(name, drop, cancellationToken);
    }

    private void SetDroppedCollectionIdentity(string name, ClearEntry drop)
    {
        var update = Command(TursoStatements.UpdateCollection, name);
        update.Parameters.AddWithValue("$created", drop.Version.Stamp.Milliseconds);
        update.Parameters.AddWithValue("$entityId", DroppedCollectionId(drop));
        update.ExecuteNonQuery();
    }

    // Worker-only: drops a client collection's table, documents and states. Dropping the table drops its indexes too.
    private void DropTable(string name)
    {
        using (var table = PlainCommand($"DROP TABLE {Table(name)};"))
        {
            table.ExecuteNonQuery();
        }

        RemoveMerkleStates(name);
        Command(TursoStatements.DropStates, name).ExecuteNonQuery();
        Command(TursoStatements.DropCollectionStats, name).ExecuteNonQuery();
        foreach (var key in _pending.Keys.Where(key => key.Collection == name).ToList())
        {
            _pending.Remove(key);
        }
    }

    public async ValueTask<CommitResult> CommitAsync(CancellationToken cancellationToken)
    {
        var result = await RunAsync(() =>
        {
            if (_faulted || Aborted)
            {
                throw new InvalidOperationException("an operation of this transaction failed, so it can only be rolled back");
            }

            RefuseKnownFault();

            long offset;
            var committing = false;
            try
            {
                FlushStates();
                DropUniqueAffected();
                offset = _offset ?? GetOffset();
                cancellationToken.ThrowIfCancellationRequested();
                RefuseKnownFault();
                committing = true;
                transaction.Commit();
                _committed = true;
                if (_collected)
                {
                    store.Collected();
                }

                if (_rewriteUncaptured)
                {
                    store.UncapturedCommit(_uncapturedBy);
                }
            }
            catch
            {
                _faulted = true;
                if (committing)
                {
                    if (_rewriteUncaptured)
                    {
                        store.UncapturedCommit(_uncapturedBy);
                    }
                    // An error at or after COMMIT, a failed sync included: whether the file holds the commit is unknown.
                    NoteCopyFault("an error at or after COMMIT");
                }

                // The driver can throw after the commit landed. Both only drop caches: safe whether or not it did.
                if (committing && ChangedReaderState)
                {
                    store.ForgetCollections();
                }

                if (committing && (_offset is not null || _feedChanged))
                {
                    store.MoveCommitCount();
                }

                throw;
            }

            // On the write thread right after the commit, so readers see stale collection names only
            // briefly. Every step runs, each even when one before it failed.
            CommitResult result;
            try
            {
                result = new CommitResult(offset, IsClient && _captured > 0 && !_captureSuspended ? ReadCommitMarker() : null);
            }
            catch
            {
                // The commit landed but its final CDC row could not be read: the log of this file is not what it wrote.
                NoteCopyFault("the commit marker could not be read after COMMIT");
                if (_offset is not null || _feedChanged)
                {
                    Follow("the commit notification", committed);
                }

                NotifyReplicationCommitted();
                throw;
            }

            Committed = result;
            Follow("the after-commit hook", store.AfterTursoCommit);
            if (ChangedReaderState)
            {
                Follow("forgetting the collection names", store.ForgetCollections);
            }

            if (_statisticsChanged)
            {
                // Only query plans depend on it: a session opened before the ANALYZE plans without the new statistics.
                Follow("moving the statistics generation", store.StatisticsChanged);
            }

            return result;
        }, cancellationToken).ConfigureAwait(false);

        // A failed wake-up only delays its waiter, which also wakes on its own timeout.
        if (_offset is not null || _feedChanged)
        {
            Follow("the commit notification", committed);
        }

        // Placement readers must observe the advanced catalog cache generation when this wake arrives.
        NotifyReplicationCommitted();
        if (_followFailure is { } failure)
        {
            ExceptionDispatchInfo.Throw(failure);
        }

        // Damage may be observed by a concurrent reader or an after-commit hook while native COMMIT runs.
        // The durable intent preserves the commit; a known fault must not return a successful acknowledgement.
        RefuseKnownFault();
        return result;
    }

    private void RefuseKnownFault()
    {
        if (write && store.Ledger?.FailedClosed is { } failed)
        {
            throw new ReplicaCopyFaultedException($"storage failed closed: {failed}");
        }

        if (write && store.CopyFault is { } copyFault)
        {
            throw new ReplicaCopyFaultedException($"this copy is faulted: {copyFault}");
        }
    }

    private const string CommitMarkerSql = "SELECT change_id, change_type FROM turso_cdc ORDER BY change_id DESC LIMIT 1;";

    // Set once this transaction ran a statement through PlainCommand, which is where every DDL runs.
    private bool _ranPlain;

    // Worker-only, after COMMIT and before releasing the exclusive writer; never read a later connection's head.
    private CommitMarker ReadCommitMarker()
    {
        if (store.Tenant is { } tenant)
        {
            store.Checkpoint?.Invoke("writer.marker", tenant);
        }

        // Kept unless this transaction ran DDL: preparing this statement cost more than the rest of a small write's statements.
        var kept = _ranPlain ? null : session.AfterCommit(CommitMarkerSql, transaction);
        using var fresh = kept is null ? session.Connection.CreateCommand() : null;
        fresh?.CommandText = CommitMarkerSql;

        using var reader = (kept ?? fresh!).ExecuteReader();
        if (!reader.Read() || reader.GetInt64(0) <= 0 || reader.GetInt64(1) != 2)
        {
            throw new InvalidDataException("a client mutation has no final CDC commit row");
        }

        return new CommitMarker(store.Incarnation, reader.GetInt64(0));
    }

    private void NotifyReplicationCommitted()
    {
        // Remote catalog joins can change placement; ordinary joined states are never shipped again.
        if (_offset is not null && (store.IsSystem || (_captured > 0 && !_captureSuspended)))
        {
            Follow("the replication notification", store.ReplicationCommitted);
        }
    }

    // The first step after the commit that failed: CommitAsync throws it once every step ran.
    private Exception? _followFailure;

    // Runs one step after the commit landed; its failure is logged and kept instead of skipping the steps after it.
    private void Follow(string step, Action? action)
    {
        try
        {
            action?.Invoke();
        }
        catch (Exception error)
        {
            _followFailure ??= error;
            store.LogAfterCommit(step, error);
        }
    }

    // Savepoints are lazy: SAVEPOINT runs with the write's first statement and RELEASE with the next write's, so a
    // batched write pays no extra thread hop for them.
    private bool _savepointPending;
    private bool _releasePending;

    /// <summary>Whether the current request added client state rows since its savepoint, after flushing them.</summary>
    internal bool SavepointHasChanges => _savepoint is { } saved && _captured > saved.Captured;

    /// <summary>Starts the savepoint one queued write of a group commit runs in. Runs no statement yet.</summary>
    internal void Savepoint()
    {
        ThrowIfReadOnly();
        _savepoint = (_changeBytes, _captured, _writes, _writtenBytes, _offset, ChangedReaderState);
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
        (long ChangeBytes, int Captured, int Writes, long WrittenBytes, long? Offset, bool CollectionsChanged) saved)
    {
        _pending.Clear();
        // A collection the rolled-back write created is gone again.
        _tables = null;
        (_changeBytes, _captured, _writes, _writtenBytes, _offset, ChangedReaderState) =
            (saved.ChangeBytes, saved.Captured, saved.Writes, saved.WrittenBytes, saved.Offset, saved.CollectionsChanged);
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

    /// <summary>
    /// Set once Turso rolled the whole transaction back after a failed statement, every savepoint included: it can only
    /// be disposed.
    /// </summary>
    internal bool Aborted { get; private set; }

    // Worker-only: runs a call of this transaction, and notices when a failure ended it.
    private T Guarded<T>(Func<T> call)
    {
        if (Aborted)
        {
            throw new InvalidOperationException("a failed statement rolled this transaction back, so it can only be disposed");
        }

        try
        {
            return call();
        }
        // Whatever surfaced: a stopped read arrives as a QueryCapacityException or an OperationCanceledException.
        catch (Exception error) when (NoteDamage(error) || Ended())
        {
            Aborted = _faulted = true;
            throw;
        }
    }

    // Unlike SQLite, Turso rolls the whole transaction back when a statement fails while it steps (a stopped read, for
    // one). The driver then marks the transaction ended (src/vendor/Turso.Data.Sqlite, patch 0003) and refuses the
    // commands bound to it; preparing one runs nothing.
    private bool Ended()
    {
        using var probe = session.Connection.CreateCommand();
        probe.Transaction = transaction;
        probe.CommandText = "SELECT 1;";
        try
        {
            probe.Prepare();
            return false;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }

    private async ValueTask RunVoidAsync(Action call) => await RunAsync(() =>
    {
        call();
        return true;
    }, CancellationToken.None).ConfigureAwait(false);

    private bool _captureSuspended;
    private bool _rewriteUncaptured;
    private string? _uncapturedBy;

    /// <summary>
    /// Stops capturing this write transaction's later statements in <c>turso_cdc</c> (design C's per-transaction toggle,
    /// docs/terrabase/multi-region-replication.md section 7); capture resumes when the transaction ends. Only for state
    /// that a restore and a replica rebuild themselves, such as the change tailer's own bookkeeping: continuous backup
    /// ships captured changes only, so anything written uncaptured is missing from every point-in-time restore.
    /// Only writes confined to tables refreshed or discarded at the rewrite's final cut pass rewriteRefreshOnly.
    /// </summary>
    internal ValueTask SuspendCaptureAsync(CancellationToken cancellationToken, bool rewriteRefreshOnly = false, [CallerMemberName] string? caller = null)
    {
        if (!write)
        {
            throw new InvalidOperationException("only a write transaction captures changes");
        }

        if (!store.Captures)
        {
            return ValueTask.CompletedTask;
        }

        return new(RunAsync(() =>
        {
            using var command = session.Connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = TursoSession.CaptureOff;
            command.ExecuteNonQuery();
            _captureSuspended = true;
            _rewriteUncaptured |= !rewriteRefreshOnly;
            if (!rewriteRefreshOnly)
            {
                _uncapturedBy = caller;
            }
            return true;
        }, cancellationToken).AsTask());
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
                        session.TrimTableCommands();
                        if (write && !_committed)
                        {
                            session.ForgetSchema();
                        }
                    }
                    finally
                    {
                        // Also when a failed statement ended it: the driver then knows, and rolls nothing back.
                        transaction.Dispose();
                    }

                    if (write && store.Tenant is { } tenant)
                    {
                        // Integration tests make the writer's reset fail here; production's checkpoint does nothing.
                        store.Checkpoint?.Invoke("writer.reset", tenant);
                    }

                    if (_captureSuspended)
                    {
                        // A failed reset closes the writer: the next one opens with capture on.
                        using var capture = session.Connection.CreateCommand();
                        capture.CommandText = TursoSession.CaptureOn;
                        capture.ExecuteNonQuery();
                    }

                    if (!write && store.Tenant is { } readingTenant)
                    {
                        // Integration tests make the reader's reset fail here: the session is then closed, not pooled.
                        store.Checkpoint?.Invoke("reader.reset", readingTenant);
                    }
                }
                // NoteDamage only notes a damaged file's error, so its fault is recorded before the writer is released.
                catch (Exception error) when (NoteDamage(error) || _committed)
                {
                    // The write committed and synced: only this session is lost, so the commit is not reported as failed.
                    store.Discard(session);
                    return;
                }
                catch
                {
                    store.Discard(session);
                    throw;
                }

                // Still admitted until pooled or closed, including an eviction checkpoint.
                store.Return(session);
            }, CancellationToken.None).ConfigureAwait(false);
            Operation?.Complete();
        }
        finally
        {
            try
            {
                if (_copyFault is { } reason)
                {
                    // Recorded before the writer is released: no later writer of this process uses the file unrefused.
                    await store.RecordCopyFaultAsync(reason).ConfigureAwait(false);
                }
            }
            finally
            {
                try
                {
                    await store.EndAsync(write, ownsAdmission, Operation).ConfigureAwait(false);
                }
                finally
                {
                    try
                    {
                        _stateReadMemory?.Dispose();
                    }
                    finally
                    {
                        _gate.Release();
                    }
                }
            }
        }
    }

    private long GetOffset() => (long?)session.Command(TursoStatements.CurrentOffset, transaction).ExecuteScalar() ?? 0;

    // Every write in one transaction shares one offset, allocated under the database writer gate.
    private long Offset()
    {
        if (_offset is null)
        {
            var current = GetOffset();
            VersionClock.EnsureCanAllocate(current);
            var next = session.Command(TursoStatements.SetOffset, transaction);
            next.Parameters.AddWithValue("$value", current == long.MaxValue ? throw new OverflowException("the commit offset is exhausted") : current + 1);
            _offset = next.ExecuteNonQuery() == 1 ? current + 1 : throw new OverflowException("the commit offset is exhausted");
        }

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

    // retry: journal reads use the session's cancellable busy handling (TursoSession.ExecuteAsync).
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
                    return Guarded(call);
                }, cancellationToken).ConfigureAwait(false);
            }

            return await runner.RunAsync(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                RunPendingSavepoint();
                // Turso cannot interrupt a running statement: writes cancel between statements.
                var result = Guarded(call);
                if (mutation)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                return result;
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException error) when (TursoSession.Stopped(error) && cancellationToken.IsCancellationRequested)
        {
            if (mutation)
            {
                _faulted = true;
            }

            throw new OperationCanceledException("the storage operation was cancelled", error, cancellationToken);
        }
        catch when (_copyFault is not null)
        {
            // Durable before the caller sees the damage: a read records no intent, so a crash from here still faults the copy.
            await store.Flagged.ConfigureAwait(false);
            if (mutation)
            {
                _faulted = true;
            }

            throw;
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
        var command = session.TableCommand(sql, transaction);
        command.Parameters.AddWithValue("$collection", collection);
        command.Parameters.AddWithValue("$id", id);
        return command;
    }

    // Called right before a physical SELECT over document or feed rows, so misses and empty pages count too.
    private void CountRead(string path) => store.StorageReads?.Add(1, new KeyValuePair<string, object?>("path", path));

    // Native octet_length projections inspect size without copying JSON; GetBytes/GetStream would allocate first.
    private void ReserveJson(long size)
    {
        QosContext.ReserveResult(checked(size * 3 + 2048), store.IsSystem ? null : QosContext.Current);
    }

    private SqliteCommand Uncached(string collection)
    {
        var command = session.Connection.CreateCommand();
        command.Transaction = transaction;
        command.Parameters.AddWithValue("$collection", collection);
        return command;
    }

}


using System.Collections.Frozen;
using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using TerraBase.Configuration;
using TerraBase.Features.Memory;
using TerraBase.Features.Qos;
using TerraBase.Primitives;
using TerraBase.Tenancy;

namespace TerraBase.Engine.Sqlite;

/// <param name="name">Names the database in error messages: its id, or <c>_system</c>.</param>
/// <param name="path">The database file.</param>
/// <param name="pool">Idle sessions, shared by every database of the engine.</param>
/// <param name="io">The bounded SQLite worker pool.</param>
/// <param name="pragmas">Connection pragmas applied to each session.</param>
/// <param name="tenant">The storage identity; null for the system database.</param>
/// <param name="time">The clock of the query deadline.</param>
/// <param name="changesCommitted">Receives the last captured sequence after a write commits.</param>
/// <param name="committed">Optional engine notification after a successful commit that changed this database.</param>
/// <param name="logger">Receives the warning about a long wait for this database's writer.</param>
/// <param name="schemaMemory">Shared process admission for schema evaluation, when hosted.</param>
/// <param name="schemaEvaluations">Host-owned counter of collection schema keyword evaluations.</param>
internal sealed partial class SqliteDatabaseStore(
    string name, string path, SqliteSessionPool pool, SqliteIoPool io, string pragmas, TenantContext? tenant, TimeProvider? time,
    Action<long>? changesCommitted, Action? committed, ILogger? logger = null, IMemoryBudget? schemaMemory = null, Counter<long>? schemaEvaluations = null) : IDatabaseStore, IDisposable
{
    internal IMemoryBudget? SchemaMemory { get; } = schemaMemory;
    internal Counter<long>? SchemaEvaluations { get; } = schemaEvaluations;

    internal SqliteDatabaseStore(string name, string path, SqliteSessionPool pool, SqliteIoPool io, string pragmas, TimeProvider? time = null, Action? committed = null)
        : this(name, path, pool, io, pragmas, null, time, null, committed) { }

    /// <summary>On-disk schema version, stored in <c>PRAGMA user_version</c>.</summary>
    private const int SchemaVersion = 1;

    /// <summary>
    /// The storage format's identity: SHA-256 of the schema every database file is bootstrapped with. Version numbers never
    /// increase (docs/design/anna-core.md decision 9), so this is what tells two formats apart; any change to the bootstrap
    /// SQL is a format change.
    /// </summary>
    internal static readonly byte[] FormatDigest = SHA256.HashData(Encoding.UTF8.GetBytes(SchemaSql + UniqueSchemaSql));

    // Greenfield bootstrap only: an older on-disk shape must be discarded, never upgraded.
    private const string SchemaSql = $$"""
        CREATE TABLE documents (
            collection TEXT NOT NULL, id TEXT NOT NULL, version INTEGER NOT NULL, body TEXT,
            PRIMARY KEY (collection, id)
        ) WITHOUT ROWID;
        CREATE INDEX documents_live ON documents (collection, id) WHERE body IS NOT NULL;
        CREATE TABLE collections (
            name TEXT PRIMARY KEY, created_at INTEGER NOT NULL, generation INTEGER NOT NULL,
            entity_id TEXT NOT NULL CHECK (length(entity_id) = 26)
        ) WITHOUT ROWID;
        CREATE TABLE commit_offset (
            id INTEGER PRIMARY KEY CHECK (id = 1), value INTEGER NOT NULL, incarnation TEXT NOT NULL
        );
        INSERT INTO commit_offset (id, value, incarnation) VALUES (1, 0, '');
        CREATE TABLE shape_history (id INTEGER PRIMARY KEY CHECK (id = 1), floor INTEGER NOT NULL);
        INSERT INTO shape_history (id, floor) VALUES (1, 0);
        CREATE TABLE change_log (
            sequence INTEGER PRIMARY KEY, collection TEXT NOT NULL, generation INTEGER NOT NULL,
            id TEXT NOT NULL, version INTEGER NOT NULL, body TEXT, document_version INTEGER NOT NULL
        );
        CREATE INDEX change_log_by_offset ON change_log (collection, generation, version, sequence);
        CREATE INDEX change_log_by_document ON change_log (collection, generation, id, version, sequence);
        CREATE TABLE change_log_births (
            collection TEXT NOT NULL, generation INTEGER NOT NULL, id TEXT NOT NULL, version INTEGER NOT NULL,
            PRIMARY KEY (collection, generation, id, version)
        ) WITHOUT ROWID;
        CREATE TABLE change_log_high (
            collection TEXT NOT NULL, generation INTEGER NOT NULL, version INTEGER NOT NULL, id TEXT NOT NULL,
            PRIMARY KEY (collection, generation, version, id)
        ) WITHOUT ROWID;
        CREATE TRIGGER change_log_birth AFTER INSERT ON change_log
        WHEN NOT EXISTS (
            SELECT 1 FROM change_log_births b WHERE b.collection = NEW.collection AND b.generation = NEW.generation AND b.id = NEW.id)
        BEGIN
            INSERT INTO change_log_births (collection, generation, id, version) VALUES (NEW.collection, NEW.generation, NEW.id, NEW.version);
            INSERT INTO change_log_high (collection, generation, version, id)
                SELECT NEW.collection, NEW.generation, NEW.version, NEW.id
                WHERE NEW.id > coalesce((
                    SELECT h.id FROM change_log_high h WHERE h.collection = NEW.collection AND h.generation = NEW.generation
                    ORDER BY h.version DESC, h.id DESC LIMIT 1), '');
        END;
        CREATE TABLE changes (
            sequence INTEGER NOT NULL, ordinal INTEGER NOT NULL, collection TEXT NOT NULL, id TEXT NOT NULL,
            version INTEGER NOT NULL, body TEXT, PRIMARY KEY (sequence, ordinal)
        ) WITHOUT ROWID;
        CREATE INDEX changes_deleted ON changes (collection, id, version) WHERE body IS NULL;
        CREATE TABLE replication_state (id INTEGER PRIMARY KEY CHECK (id = 1), complete INTEGER NOT NULL);
        INSERT INTO replication_state (id, complete) VALUES (1, 1);
        CREATE TABLE applied (
            origin BLOB PRIMARY KEY, sequence INTEGER NOT NULL,
            digest BLOB CHECK (digest IS NULL OR length(digest) = 32)
        ) WITHOUT ROWID;
        CREATE TABLE journal_prefixes (
            sequence INTEGER PRIMARY KEY CHECK (sequence > 0), digest BLOB NOT NULL CHECK (length(digest) = 32)
        ) WITHOUT ROWID;
        CREATE INDEX documents_replication_keys ON documents (collection, id)
            WHERE {{SqliteStatements.BoundedReplicationKeys}};
        CREATE INDEX changes_replication_deleted ON changes (collection, id, version)
            WHERE body IS NULL AND {{SqliteStatements.BoundedReplicationKeys}};
        """;

    private readonly string _connectionString = new SqliteConnectionStringBuilder
    {
        DataSource = path,
        // Never recreate a deleted database: only InitializeAsync creates files.
        Mode = SqliteOpenMode.ReadWrite,
        // This store pools sessions itself, so prepared statements survive between transactions.
        Pooling = false,
        // Seconds a statement waits on a locked database before failing.
        DefaultTimeout = 30,
    }.ToString();

    // Signals every write commit, collection-only ones included, to WaitForCommitAsync.
    private readonly Lock _commits = new();
    private long _sequence;
    private TaskCompletionSource _signal = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Whether this is TerraBase's own database, which keeps no shape history.</summary>
    internal bool IsSystem => name == SqliteStorageEngine.SystemName;

    public long CommitSequence
    {
        get
        {
            lock (_commits)
            {
                return _sequence;
            }
        }
    }

    public Task WaitForCommitAsync(long sequence, CancellationToken cancellationToken)
    {
        Task signal;
        lock (_commits)
        {
            if (_sequence > sequence)
            {
                return Task.CompletedTask;
            }

            signal = _signal.Task;
        }

        return signal.WaitAsync(cancellationToken);
    }

    // Writers queue here, asynchronously, rather than in BEGIN IMMEDIATE: writers blocked in SQLite's
    // busy handler would hold every I/O thread while the writer that holds the lock waits for one.
    private readonly SemaphoreSlim _writer = new(1, 1);

    /// <summary>A wait for the writer this long or longer logs who held it (<see cref="AcquireWriterAsync"/>).</summary>
    internal static readonly TimeSpan SlowWriterWait = TimeSpan.FromSeconds(5);

    // What holds _writer and since when (a TimeProvider timestamp), set by each holder once it took it. Read without the
    // gate, for the warning only.
    private string? _writerHolder;
    private long _writerTaken;

    /// <summary>The thread this database's write transactions run on, apart from the read pool.</summary>
    internal SqliteWriteLane WriteLane { get; } = new(name, io);

    // Boxed so a catalog follower can adopt its primary's incarnation without torn reads.
    private volatile StrongBox<Ulid> _incarnation = new(Ulid.Empty);

    public Ulid Incarnation => _incarnation.Value;

    /// <summary>Publishes the incarnation a catalog follower's committed snapshot adopted (docs/decisions/0009).</summary>
    internal void AdoptIncarnation(Ulid incarnation) => _incarnation = new(incarnation);

    public TenantContext? Tenant { get; } = tenant;

    // Client databases scope ETags by their ids, which every member shares, so a version read on one member matches on
    // another (TS-574); _system uses its incarnation, which catalog followers adopt from their primary.
    private readonly Guid? _scope = tenant?.VersionScope();

    /// <summary>The <see cref="IStorageTransaction.Scope"/> of this database's transactions.</summary>
    internal Guid Scope => _scope ?? Incarnation.ToGuid();

    /// <summary>Set once the database is deleted or the engine shuts down; guarded by <see cref="SqliteSessionPool"/>.</summary>
    internal bool Closed
    {
        get => Volatile.Read(ref field);
        set => Volatile.Write(ref field, value);
    }

    public bool IsClosed => Closed;

    internal TimeProvider Time => time ?? TimeProvider.System;

    internal static readonly TimeSpan DefaultQueryBudget = TimeSpan.FromSeconds(TerraBaseOptions.DefaultQueryBudgetSeconds);

    /// <summary>SQLite execution time one query may spend on <see cref="Time"/> before it is refused.</summary>
    internal TimeSpan QueryBudget { get; init; } = DefaultQueryBudget;

    /// <summary>Idle sessions of this database, for tests.</summary>
    internal int IdleCount => pool.IdleCountOf(this);

    /// <summary>
    /// Whether this is a catalog follower's system database: it refuses write transactions except
    /// <see cref="BeginReplicaAsync"/>'s (docs/decisions/0009).
    /// </summary>
    internal bool Replica { get; init; }

    public async ValueTask<IStorageTransaction> BeginTransactionAsync(bool write, CancellationToken cancellationToken) =>
        write && Replica ? throw new CatalogFollowerException() : await BeginAsync(write, capture: true, durable: false, cancellationToken).ConfigureAwait(false);

    public async ValueTask<IStorageTransaction> BeginDurableTransactionAsync(CancellationToken cancellationToken) =>
        Replica
            ? throw new CatalogFollowerException()
            : await BeginAsync(write: true, capture: true, durable: true, cancellationToken, "a durable write transaction").ConfigureAwait(false);

    /// <summary>The write transaction of a follower's replication applier; it captures nothing.</summary>
    internal ValueTask<SqliteStorageTransaction> BeginReplicaAsync(CancellationToken cancellationToken) =>
        BeginAsync(write: true, capture: false, durable: false, cancellationToken, "a catalog replica transaction");

    // writer names a write transaction in the warning about a long wait for the writer.
    private async ValueTask<SqliteStorageTransaction> BeginAsync(
        bool write, bool capture, bool durable, CancellationToken cancellationToken, string writer = "a write transaction")
    {
        var ownsAdmission = !io.HasAdmission;
        if (ownsAdmission)
        {
            io.Admit();
        }
        var writing = false;
        SqliteSession? session = null;
        var beginStarted = false;
        SqliteRunner runner = write ? WriteLane : io;
        try
        {
            if (write)
            {
                await AcquireWriterAsync(writer, cancellationToken).ConfigureAwait(false);
                writing = true;
            }

            session = await RentAsync(runner, cancellationToken).ConfigureAwait(false);
            var connection = session.Connection;
            var transaction = await session.ExecuteAsync(runner, () =>
            {
                beginStarted = true;
                // BEGIN DEFERRED alone still permits a write upgrade. SQLite enforces read-only
                // access before BEGIN; both setup and native lock acquisition share the caller's budget.
                if (!write)
                {
                    using var readOnly = connection.CreateCommand();
                    readOnly.CommandText = "PRAGMA query_only = ON;";
                    readOnly.ExecuteNonQuery();
                }

                // SQLite refuses to change it inside a transaction; with FULL the commit syncs the WAL (decision 0010).
                if (durable)
                {
                    using var full = connection.CreateCommand();
                    full.CommandText = "PRAGMA synchronous = FULL;";
                    full.ExecuteNonQuery();
                }

                // Writers take the lock up front (BEGIN IMMEDIATE) so they wait instead of failing on upgrade.
                return connection.BeginTransaction(deferred: !write);
            }, cancellationToken).ConfigureAwait(false);
            return new SqliteStorageTransaction(
                this, session, transaction, runner, write, durable, write ? Committed : null, write && capture ? changesCommitted : null, ownsAdmission);
        }
        catch
        {
            try
            {
                if (session is not null && (beginStarted || !pool.TryReturn(this, session)))
                {
                    // A never-started BEGIN leaves the session untouched. If it cannot fit back in
                    // the idle pool, retain admission until its worker-owned close has finished.
                    await runner.RunAsync(() => pool.Discard(session), CancellationToken.None).ConfigureAwait(false);
                }
            }
            finally
            {
                End(writing, ownsAdmission);
            }

            throw;
        }
    }

    private void Committed()
    {
        committed?.Invoke();
        TaskCompletionSource signal;
        lock (_commits)
        {
            _sequence++;
            signal = _signal;
            _signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        signal.SetResult();
    }

    // Takes the writer, waiting at most 30 s as busy_timeout did before this wait replaced it in-process. A wait of
    // SlowWriterWait or more, whatever its outcome, logs what held the writer, for how long, and whether it was running a
    // storage call or between calls (its caller busy elsewhere): the evidence a busy or cancelled writer needs.
    private async ValueTask AcquireWriterAsync(string writer, CancellationToken cancellationToken)
    {
        var started = Time.GetTimestamp();
        var outcome = "and took it";
        try
        {
            if (!await _writer.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false))
            {
                outcome = "and gave up";
                throw io.Busy();
            }
        }
        catch (OperationCanceledException)
        {
            outcome = "until it was cancelled";
            throw;
        }
        finally
        {
            if (logger is not null && Time.GetElapsedTime(started) is var waited && waited >= SlowWriterWait)
            {
                var holder = Volatile.Read(ref _writerHolder) ?? "nothing recorded";
                var held = Time.GetElapsedTime(Volatile.Read(ref _writerTaken));
                LogSlowWriterWait(
                    logger, Math.Round(waited.TotalSeconds, 1), outcome, writer, holder, Math.Round(held.TotalSeconds, 1),
                    WriteLane.Running ? "running a storage call" : "between storage calls");
            }
        }

        // Only this holder writes these until it releases the writer.
        Volatile.Write(ref _writerTaken, Time.GetTimestamp());
        Volatile.Write(ref _writerHolder, writer);
    }

    // Counts and kinds only: no database or tenant names in the log.
    [LoggerMessage(Level = LogLevel.Warning, Message =
        "Waited {Waited} s for a database's writer {Outcome}: {Writer} queued behind {Holder}, which had held it for {Held} s and was {State}")]
    private static partial void LogSlowWriterWait(ILogger logger, double waited, string outcome, string writer, string holder, double held, string state);

    /// <summary>Releases what <see cref="BeginTransactionAsync"/> took; called once per transaction.</summary>
    internal void End(bool write, bool ownsAdmission)
    {
        if (write)
        {
            _writer.Release();
        }

        if (ownsAdmission)
        {
            io.Release();
        }
    }

    /// <summary>Writes a consistent snapshot of this database to a new file at <paramref name="path"/>; the caller holds an admission.</summary>
    internal async Task CopyToAsync(string path, CancellationToken cancellationToken)
    {
        var session = await RentAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await io.RunAsync(() =>
            {
                using var command = session.Connection.CreateCommand();
                command.CommandText = "VACUUM INTO $path;";
                command.Parameters.AddWithValue("$path", path);
                command.ExecuteNonQuery();
            }, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await io.RunAsync(() => pool.Discard(session), CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        await ReturnAsync(session).ConfigureAwait(false);
        // InitializeAsync switches the copy to WAL and refuses an unsupported schema version.
        await InitializeAsync(name, path, io, pragmas, CopyReset, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Makes a copy of a database a new one: its replication starts over from a baseline.</summary>
    internal const string CopyReset = SqliteStatements.MaterializeRetainedDeletions + """
        UPDATE replication_state SET complete = 0 WHERE id = 1;
        DELETE FROM changes;
        DELETE FROM journal_prefixes;
        DELETE FROM applied;
        """;

    /// <summary>The database file.</summary>
    internal string FilePath => path;

    /// <summary>
    /// Runs <paramref name="call"/> while no write transaction of this process is open on this database; writers queue
    /// meanwhile. Continuous backup reads the log and checkpoints it under this (TS-633).
    /// </summary>
    internal async Task<T> WithWritersHeldAsync<T>(Func<Task<T>> call, CancellationToken cancellationToken)
    {
        await AcquireWriterAsync("the continuous backup's checkpoint", cancellationToken).ConfigureAwait(false);
        try
        {
            return await call().ConfigureAwait(false);
        }
        finally
        {
            _writer.Release();
        }
    }

    /// <summary>Rents a session for work outside a transaction (continuous backup); the caller holds an admission.</summary>
    // Group commit (docs/design/anna-core.md section 4.1): client writes queue here, and one loop at a time runs a batch
    // of them in one write transaction, each inside its own savepoint, so the database's single SQLite writer commits
    // once per batch instead of once per write.
    // anna-full (TS-648): per-core actors plan writes in parallel and hand this writer ready batches.
    private const int MaxBatch = 256;
    private readonly Queue<IBatchedWrite> _queued = [];
    private readonly Lock _queueGate = new();
    private bool _batching;

    // Writes of the batch that is running, for tests.
    private int _batched;

    /// <summary>Writes of the batch that is running (waiting for the writer or committing), for tests.</summary>
    internal int BatchedWrites => Volatile.Read(ref _batched);

    /// <summary>Writes waiting for the next batch, for tests.</summary>
    internal int QueuedWrites
    {
        get
        {
            lock (_queueGate)
            {
                return _queued.Count;
            }
        }
    }

    public async ValueTask<(T Result, long Offset)> WriteAsync<T>(
        Func<IStorageTransaction, CancellationToken, ValueTask<T>> work, Func<T, bool> keep, CancellationToken cancellationToken)
    {
        // A queued write holds an admission like a transaction does, so storage-queue still bounds waiting writes.
        var ownsAdmission = !io.HasAdmission;
        if (ownsAdmission)
        {
            io.Admit();
        }

        try
        {
            var write = new BatchedWrite<T>(work, keep, cancellationToken);
            bool start;
            lock (_queueGate)
            {
                _queued.Enqueue(write);
                start = !_batching;
                _batching = true;
            }

            if (start)
            {
                if (ExecutionContext.IsFlowSuppressed())
                {
                    _ = Task.Run(RunBatchesAsync, CancellationToken.None);
                }
                else
                {
                    using (ExecutionContext.SuppressFlow())
                    {
                        _ = Task.Run(RunBatchesAsync, CancellationToken.None);
                    }
                }
            }

            return await write.Task.ConfigureAwait(false);
        }
        finally
        {
            if (ownsAdmission)
            {
                io.Release();
            }
        }
    }

    private async Task RunBatchesAsync()
    {
        List<IBatchedWrite> deferred = [];
        while (true)
        {
            var batch = deferred;
            lock (_queueGate)
            {
                while (batch.Count < MaxBatch && _queued.TryDequeue(out var write))
                {
                    batch.Add(write);
                }

                if (batch.Count == 0)
                {
                    _batching = false;
                    return;
                }

                _batched = batch.Count;
            }

            // The queued writes' admissions cover the batch's transaction.
            deferred = await io.WithAdmissionAsync(() => CommitBatchAsync(batch)).ConfigureAwait(false);
            Volatile.Write(ref _batched, 0);
        }
    }

    // Runs one batch and answers its writes once it committed. Returns the writes that a per-commit limit refused only
    // because others shared the transaction: they lead the next batch.
    private async ValueTask<List<IBatchedWrite>> CommitBatchAsync(List<IBatchedWrite> batch)
    {
        List<IBatchedWrite> kept = [];
        List<IBatchedWrite> refused = [];
        List<IBatchedWrite> deferred = [];
        long offset = 0;
        try
        {
            var transaction = await BeginAsync(write: true, capture: true, durable: false, CancellationToken.None, "a group commit batch").ConfigureAwait(false);
            await using (transaction.ConfigureAwait(false))
            {
                foreach (var write in batch)
                {
                    if (write.Cancelled)
                    {
                        write.Cancel();
                        continue;
                    }

                    transaction.Savepoint();
                    bool keeps;
                    try
                    {
                        keeps = await write.RunAsync(transaction).ConfigureAwait(false);
                    }
                    catch (Exception error)
                    {
                        await transaction.RollbackToSavepointAsync().ConfigureAwait(false);
                        if (transaction.LimitsReached && kept.Count > 0)
                        {
                            deferred.Add(write);
                        }
                        else
                        {
                            write.Fail(error);
                        }

                        continue;
                    }

                    if (keeps)
                    {
                        transaction.ReleaseSavepoint();
                        kept.Add(write);
                        continue;
                    }

                    await transaction.RollbackToSavepointAsync().ConfigureAwait(false);
                    (transaction.LimitsReached && kept.Count > 0 ? deferred : refused).Add(write);
                }

                if (kept.Count > 0)
                {
                    offset = await transaction.CommitAsync(CancellationToken.None).ConfigureAwait(false);
                }
            }
        }
        catch (Exception error)
        {
            // Nothing of this batch committed: every write not answered yet fails with it.
            batch.ForEach(write => write.Fail(error));
            return [];
        }

        // After the transaction released its session, as an admission's resource must be (SqliteIoPool).
        refused.ForEach(write => write.Answer(0));
        kept.ForEach(write => write.Answer(offset));
        return deferred;
    }

    private interface IBatchedWrite
    {
        bool Cancelled { get; }

        ValueTask<bool> RunAsync(IStorageTransaction transaction);

        void Answer(long offset);

        void Fail(Exception error);

        void Cancel();
    }

    private sealed class BatchedWrite<T>(
        Func<IStorageTransaction, CancellationToken, ValueTask<T>> work, Func<T, bool> keep, CancellationToken cancellationToken) : IBatchedWrite
    {
        private readonly TaskCompletionSource<(T, long)> _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private T _result = default!;
        private readonly QosContext.Snapshot _context = QosContext.Capture();

        public Task<(T Result, long Offset)> Task => _done.Task;

        public bool Cancelled => cancellationToken.IsCancellationRequested;

        public async ValueTask<bool> RunAsync(IStorageTransaction transaction)
        {
            // Borrow request ownership while retaining the original batch's admission across awaits.
            using var scope = QosContext.Restore(_context);
            _result = await work(transaction, cancellationToken).ConfigureAwait(false);
            return keep(_result);
        }

        public void Answer(long offset) => _done.TrySetResult((_result, offset));

        public void Fail(Exception error) => _done.TrySetException(error);

        public void Cancel() => _done.TrySetCanceled(cancellationToken);
    }

    // The collection names of this database, loaded on first use and forgotten by every commit that changes them, so
    // point operations do not open an SQLite read transaction to find their collection.
    private volatile FrozenSet<string>? _collections;
    private long _collectionsVersion;

    public async ValueTask<bool> HasCollectionAsync(string name, CancellationToken cancellationToken)
    {
        if (_collections is { } known)
        {
            return known.Contains(name);
        }

        var version = Interlocked.Read(ref _collectionsVersion);
        var names = new HashSet<string>(StringComparer.Ordinal);
        var transaction = await BeginAsync(write: false, capture: false, durable: false, cancellationToken).ConfigureAwait(false);
        await using (transaction.ConfigureAwait(false))
        {
            string? after = null;
            while (true)
            {
                var (page, hasMore) = await transaction.ListCollectionsAsync(after, 1000, cancellationToken).ConfigureAwait(false);
                foreach (var collection in page)
                {
                    names.Add(collection.Name);
                }

                if (!hasMore || page.Count == 0)
                {
                    break;
                }

                after = page[^1].Name;
            }
        }

        var loaded = names.ToFrozenSet(StringComparer.Ordinal);

        // A commit that changed the collections while this load ran leaves the set unknown instead of stale.
        lock (_queueGate)
        {
            if (Interlocked.Read(ref _collectionsVersion) == version)
            {
                _collections = loaded;
            }
        }

        return loaded.Contains(name);
    }

    /// <summary>Called by a commit that created or dropped a collection.</summary>
    internal void ForgetCollections()
    {
        lock (_queueGate)
        {
            Interlocked.Increment(ref _collectionsVersion);
            _collections = null;
        }
    }

    internal Task<SqliteSession> RentSessionAsync(CancellationToken cancellationToken) => RentAsync(cancellationToken);

    /// <summary>Gives back a session from <see cref="RentSessionAsync"/>; a closed store closes it.</summary>
    internal Task ReturnSessionAsync(SqliteSession session) => ReturnAsync(session);

    /// <summary>
    /// When the change log does not cover a copied database's state, appends the current state of every document and tombstone as baseline change groups, one
    /// bounded group per transaction, then marks the log complete. Merging is idempotent, so a baseline
    /// repeated after a crash only costs bytes. Returns the last appended sequence (0 when none) and the
    /// pre-existing states skipped for failing replication admission.
    /// </summary>
    internal async Task<(long Sequence, int Skipped)> SeedAsync(CancellationToken cancellationToken)
    {
        var (sequence, skipped) = (0L, 0);
        foreach (var tombstones in new[] { false, true })
        {
            (string Collection, string Id)? after = null;
            while (true)
            {
                var transaction = (SqliteStorageTransaction)await BeginTransactionAsync(write: true, cancellationToken).ConfigureAwait(false);
                await using (transaction.ConfigureAwait(false))
                {
                    if (sequence == 0 && after is null && !tombstones)
                    {
                        if (await transaction.IsCompleteAsync(cancellationToken).ConfigureAwait(false))
                        {
                            return (0, 0);
                        }

                        // SQL skips oversized keys without bringing them into managed memory or the resume cursor.
                        skipped = await transaction.CountOversizedBaselineKeysAsync(cancellationToken).ConfigureAwait(false);
                    }

                    if (await transaction.AppendBaselineAsync(tombstones, after, cancellationToken).ConfigureAwait(false) is not { } chunk)
                    {
                        break;
                    }

                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                    after = chunk.Last;
                    sequence = Math.Max(sequence, chunk.Sequence);
                    skipped += chunk.Skipped;
                }
            }
        }

        var complete = (SqliteStorageTransaction)await BeginTransactionAsync(write: true, cancellationToken).ConfigureAwait(false);
        await using (complete.ConfigureAwait(false))
        {
            await complete.MarkCompleteAsync(cancellationToken).ConfigureAwait(false);
            await complete.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        return (sequence, skipped);
    }

    /// <summary>Takes back a session whose work has ended; the pool keeps it idle or closes it.</summary>
    /// <remarks>Call on an I/O thread: an eviction can checkpoint the WAL of another database.</remarks>
    internal void Return(SqliteSession session) => pool.Return(this, session);

    /// <summary>Closes a failed rented session; call only on an I/O worker.</summary>
    internal void Discard(SqliteSession session) => pool.Discard(session);

    private Task ReturnAsync(SqliteSession session) => io.RunAsync(() => Return(session), CancellationToken.None);

    /// <summary>
    /// Closes idle sessions and refuses new ones; a rented session is closed when it is returned.
    /// The engine calls this when the database is deleted and at shutdown.
    /// </summary>
    public void Dispose() => pool.Close(this);

    // The caller holds an admission.
    private Task<SqliteSession> RentAsync(CancellationToken cancellationToken) => RentAsync(io, cancellationToken);

    // Opens a session on runner when none is idle: write transactions open theirs on the write lane.
    private async Task<SqliteSession> RentAsync(SqliteRunner runner, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // A deleted or shut-down database hands out no session.
        if (pool.Rent(this) is { } idle)
        {
            return idle;
        }

        SqliteSession session;
        try
        {
            session = await SqliteSession.OpenAsync(runner, _connectionString, pragmas, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            // Rent counted an opening even when cancellation removed the queued Open before it started.
            pool.OpenFailed();
            if (Closed && !cancellationToken.IsCancellationRequested)
            {
                // A delete can unlink the file after Rent found no session but before SQLite opened it.
                throw new ObjectDisposedException(GetType().FullName, error);
            }

            throw;
        }

        // Adoption can close a session when shutdown or deletion won; keep it on the worker too.
        return await runner.RunAsync(() => pool.Adopt(this, session), CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>
    /// Creates (or adopts a copied) and bootstraps a database file at <paramref name="path"/> on an unpooled
    /// connection, so no handle stays open once this returns and the file can be moved. The caller holds an admission.
    /// </summary>
    /// <remarks>
    /// The file is private until the caller moves it into place, and a failed creation deletes it, so nothing is synced
    /// while it is built: one fsync of the finished file replaces the eight or so of SQLite's journal switch, commits and
    /// closing checkpoint. Creation holds the engine's creation lock, so on a slow disk those syncs queued every other
    /// creation behind them (TS-647).
    /// </remarks>
    internal static Task InitializeAsync(
        string name, string path, SqliteIoPool io, string pragmas, string? afterBootstrap, CancellationToken cancellationToken) =>
        io.RunAsync(() =>
        {
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false,
            }.ToString();
            using (var connection = new SqliteConnection(connectionString))
            {
                connection.Open();
                // A copy made by VACUUM INTO may already be current, so set WAL here and not only during bootstrap.
                // The creating connection uses the same settings as every pooled one, except that it does not sync.
                Scalar(connection, null, pragmas + "PRAGMA synchronous = OFF; PRAGMA journal_mode = WAL;");
                Bootstrap(name, connection, cancellationToken);
                // Publish a fresh identity and prepare the copied baseline atomically, on this file only.
                using var transaction = connection.BeginTransaction(deferred: false);
                RenewIncarnation(connection, transaction);
                if (afterBootstrap is not null)
                {
                    Scalar(connection, transaction, afterBootstrap);
                }

                cancellationToken.ThrowIfCancellationRequested();
                transaction.Commit();
            }

            // Closing the only connection checkpointed the log into the file and removed it: sync the file once, before
            // it can be published.
            using var file = File.OpenHandle(path, FileMode.Open, FileAccess.ReadWrite);
            RandomAccess.FlushToDisk(file);
            return true;
        }, cancellationToken);

    // The caller holds an admission.
    internal async Task BootstrapAsync(CancellationToken cancellationToken)
    {
        var session = await RentAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await session.ExecuteAsync(io, () =>
            {
                // Bootstrap is atomic and rolls back before a lock error leaves this scope.
                Bootstrap(name, session.Connection, cancellationToken);
                _incarnation = new(Ulid.Parse(
                    (string)(Scalar(session.Connection, null, SqliteStatements.Incarnation)
                        ?? throw new InvalidOperationException("database incarnation is missing"))));
                return true;
            }, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await io.RunAsync(() => pool.Discard(session), CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        await ReturnAsync(session).ConfigureAwait(false);
    }

    private static void Bootstrap(string name, SqliteConnection connection, CancellationToken cancellationToken)
    {
        var version = Version(name, connection, transaction: null);
        if (version == SchemaVersion)
        {
            return;
        }

        Scalar(connection, null, "PRAGMA journal_mode = WAL;");
        using var transaction = connection.BeginTransaction(deferred: false);
        // A concurrent initializer can finish while this connection waits for the writer lock.
        version = Version(name, connection, transaction);
        if (version == SchemaVersion)
        {
            return;
        }

        if (version != 0 || Scalar(connection, transaction, "SELECT 1 FROM sqlite_schema WHERE type = 'table' LIMIT 1;") is not null)
        {
            throw new InvalidOperationException("data-directory contains an old database format; discard the affected data before starting");
        }

        cancellationToken.ThrowIfCancellationRequested();
        Scalar(connection, transaction, SchemaSql + UniqueSchemaSql + $"PRAGMA user_version = {SchemaVersion};");
        RenewIncarnation(connection, transaction);
        cancellationToken.ThrowIfCancellationRequested();
        transaction.Commit();
    }

    // Returns the schema version (0 for an empty file); throws for any other format. Versions never increase: a format
    // change wipes storage instead, so another number is always a format this build does not read. The first build's own
    // schema 1 predates the layout record: the engine refuses its flat data directories before opening any file.
    private static int Version(string name, SqliteConnection connection, SqliteTransaction? transaction)
    {
        var version = Convert.ToInt32(
            Scalar(connection, transaction, "PRAGMA user_version;"),
            System.Globalization.CultureInfo.InvariantCulture);
        return version is 0 or SchemaVersion
            ? version
            : throw new InvalidOperationException(
                $"database '{name}' has schema version {version}, an old database format; discard the affected data before starting");
    }

    // Called only from the worker that owns this connection, including during bootstrap and copying.
    private static void RenewIncarnation(SqliteConnection connection, SqliteTransaction? transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = SqliteStatements.RenewIncarnation;
        command.Parameters.AddWithValue("$incarnation", EntityIds.New().ToString());
        command.ExecuteNonQuery();
    }

    private static object? Scalar(SqliteConnection connection, SqliteTransaction? transaction, string sql)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return command.ExecuteScalar();
    }
}

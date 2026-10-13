using System.Collections.Frozen;
using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Turso.Data.Sqlite;
using TerraBase.Configuration;
using TerraBase.Features.Memory;
using TerraBase.Features.Qos;
using TerraBase.Primitives;
using TerraBase.Tenancy;
using ZLogger;

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

    /// <summary>
    /// On-disk schema version of client and system databases alike, stored in <c>PRAGMA user_version</c>. It never
    /// increases: a format change wipes storage at a cutover instead (docs/design/anna-core.md, decision 9).
    /// </summary>
    private const int SchemaVersion = 1;

    /// <summary>
    /// The storage format's identity: SHA-256 of the schemas every database file is bootstrapped with. Version numbers
    /// never increase (docs/design/anna-core.md decision 9), so this is what tells two formats apart; any change to the
    /// bootstrap SQL is a format change.
    /// </summary>
    internal static readonly byte[] FormatDigest =
        SHA256.HashData(Encoding.UTF8.GetBytes(SystemSchemaSql + ClientSchemaSql + UniqueSchemaSql));

    // Greenfield bootstrap only: an older on-disk shape must be discarded, never upgraded. Turso stores only rowid tables
    // and its provider splits command text at every ';', so there are no WITHOUT ROWID tables and no triggers
    // (docs/terrabase/turso-spike.md). The system database keeps its
    // journal of whole writes with a SHA-256 prefix digest per group (docs/decisions/0009) until TS-647 step 5.
    private const string SystemSchemaSql = """
        CREATE TABLE documents (
            collection TEXT NOT NULL, id TEXT NOT NULL, version INTEGER NOT NULL, body TEXT,
            PRIMARY KEY (collection, id)
        );
        CREATE INDEX documents_live ON documents (collection, id) WHERE body IS NOT NULL;
        CREATE TABLE collections (
            name TEXT PRIMARY KEY, created_at INTEGER NOT NULL,
            entity_id TEXT NOT NULL CHECK (length(entity_id) = 26)
        );
        CREATE TABLE commit_offset (
            id INTEGER PRIMARY KEY CHECK (id = 1), value INTEGER NOT NULL, incarnation TEXT NOT NULL
        );
        INSERT INTO commit_offset (id, value, incarnation) VALUES (1, 0, '');
        CREATE TABLE changes (
            sequence INTEGER NOT NULL, ordinal INTEGER NOT NULL, collection TEXT NOT NULL, id TEXT NOT NULL,
            version INTEGER NOT NULL, body TEXT, PRIMARY KEY (sequence, ordinal)
        );
        CREATE INDEX changes_deleted ON changes (collection, id, version) WHERE body IS NULL;
        CREATE TABLE replication_state (id INTEGER PRIMARY KEY CHECK (id = 1), complete INTEGER NOT NULL);
        INSERT INTO replication_state (id, complete) VALUES (1, 1);
        CREATE TABLE applied (
            origin BLOB PRIMARY KEY, sequence INTEGER NOT NULL,
            digest BLOB CHECK (digest IS NULL OR length(digest) = 32)
        );
        CREATE TABLE journal_prefixes (
            sequence INTEGER PRIMARY KEY CHECK (sequence > 0), digest BLOB NOT NULL CHECK (length(digest) = 32)
        );
        """;

    // A client database (docs/terrabase/multi-region-replication.md section 5). Each collection is a table of its own, created
    // with it (the metadata collections here), holding the materialized JSON of its live documents (doc): reads, queries,
    // and indexes never decode a state. _tb_state keeps each document's replicated DocumentState in its
    // stored form (base64url), with its anti-entropy bucket and digest; the other _tb_ tables belong to the change feed,
    // the peer streams and anti-entropy, which later slices fill. Every new table is a rowid table.
    private const string ClientSchemaSql = """
        CREATE TABLE "_collections" (id TEXT NOT NULL UNIQUE, doc TEXT NOT NULL);
        CREATE TABLE "_indexes" (id TEXT NOT NULL UNIQUE, doc TEXT NOT NULL);
        CREATE TABLE "_unique" (id TEXT NOT NULL UNIQUE, doc TEXT NOT NULL);
        CREATE TABLE _tb_state (
            collection TEXT NOT NULL, id TEXT NOT NULL, bucket INTEGER NOT NULL, state TEXT NOT NULL,
            stamp INTEGER NOT NULL, origin INTEGER NOT NULL, live INTEGER NOT NULL, h1 INTEGER NOT NULL, h2 INTEGER NOT NULL,
            UNIQUE (collection, id)
        );
        CREATE INDEX _tb_state_bucket ON _tb_state (bucket, collection, id);
        CREATE INDEX _tb_state_dead ON _tb_state (stamp) WHERE live = 0;
        CREATE TABLE _tb_peers (
            node INTEGER PRIMARY KEY, applied INTEGER NOT NULL, acked INTEGER NOT NULL, stable INTEGER NOT NULL, seen INTEGER NOT NULL
        );
        CREATE TABLE _tb_feed (
            origin INTEGER NOT NULL, stamp INTEGER NOT NULL, collection TEXT NOT NULL, id TEXT NOT NULL, deleted INTEGER NOT NULL
        );
        CREATE INDEX _tb_feed_order ON _tb_feed (origin, stamp);
        CREATE TABLE _tb_feed_floor (origin INTEGER PRIMARY KEY, stamp INTEGER NOT NULL);
        CREATE TABLE _tb_merkle (bucket INTEGER PRIMARY KEY, h1 INTEGER NOT NULL, h2 INTEGER NOT NULL);
        CREATE TABLE _tb_tail (id INTEGER PRIMARY KEY CHECK (id = 1), change_id INTEGER NOT NULL);
        CREATE TABLE collections (
            name TEXT PRIMARY KEY, created_at INTEGER NOT NULL,
            entity_id TEXT NOT NULL CHECK (length(entity_id) = 26)
        );
        CREATE TABLE commit_offset (
            id INTEGER PRIMARY KEY CHECK (id = 1), value INTEGER NOT NULL, incarnation TEXT NOT NULL
        );
        INSERT INTO commit_offset (id, value, incarnation) VALUES (1, 0, '');
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

    /// <summary>Whether this is TerraBase's own database rather than a client database.</summary>
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

    /// <summary>What a client database's local writes mint with; the system database writes without it.</summary>
    internal LocalReplica? Member { get; init; }

    /// <summary>
    /// Whether this is a catalog follower's system database: it refuses write transactions except
    /// <see cref="BeginReplicaAsync"/>'s (docs/decisions/0009).
    /// </summary>
    internal bool Replica { get; init; }

    /// <summary>
    /// Whether every write transaction syncs the write-ahead log at its commit (<c>synchronous = FULL</c>), as a durable one
    /// does: the system database, whose acknowledged catalog and API keys survive an OS crash (docs/terrabase/decisions/0012).
    /// </summary>
    internal bool SyncsEveryCommit { get; init; }

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
        durable |= write && SyncsEveryCommit;
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

            session = write ? await WriterSessionAsync(cancellationToken).ConfigureAwait(false) : await RentAsync(runner, cancellationToken).ConfigureAwait(false);
            var connection = session.Connection;
            var transaction = await session.ExecuteAsync(runner, () =>
            {
                beginStarted = true;
                // BEGIN DEFERRED alone still permits a write upgrade. SQLite enforces read-only
                // access before BEGIN; both setup and native lock acquisition share the caller's budget.
                if (!write)
                {
                    using var readOnly = connection.CreateCommand();
                    readOnly.CommandText = "PRAGMA query_only = 1;";
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
                if (session is not null && write)
                {
                    // An untouched writer stays the store's; one whose BEGIN started is reopened by the next writer.
                    if (beginStarted)
                    {
                        await runner.RunAsync(() => Discard(session), CancellationToken.None).ConfigureAwait(false);
                    }
                }
                else if (session is not null && (beginStarted || !pool.TryReturn(this, session)))
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
        try
        {
            committed?.Invoke();
        }
        finally
        {
            // Even when the engine's notification failed.
            MoveCommitCount();
        }
    }

    /// <summary>
    /// Moves the commit count, a cache key of StorageQuota, and wakes its waiters. Only invalidates, so it is also safe after
    /// a commit that may not have landed.
    /// </summary>
    internal void MoveCommitCount()
    {
        TaskCompletionSource signal;
        lock (_commits)
        {
            _sequence++;
            signal = _signal;
            _signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        signal.SetResult();
    }

    /// <summary>Logs a step after a landed commit that failed; the commit's other steps still ran.</summary>
    internal void LogAfterCommit(string step, Exception error)
    {
        try
        {
            logger?.ZLogError(error, $"Database {name:@Database} committed, but {step:@Step} after its commit failed");
        }
        catch (Exception)
        {
            // A failing logger must not skip the commit's remaining steps.
        }
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
                // Counts and kinds only: no database or tenant names in the log.
                logger.ZLogWarning($"Waited {Math.Round(waited.TotalSeconds, 1):@Waited} s for a database's writer {outcome:@Outcome}: {writer:@Writer} queued behind {holder:@Holder}, which had held it for {Math.Round(held.TotalSeconds, 1):@Held} s and was {(WriteLane.Running ? "running a storage call" : "between storage calls"):@State}");
            }
        }

        // Only this holder writes these until it releases the writer.
        Volatile.Write(ref _writerTaken, Time.GetTimestamp());
        Volatile.Write(ref _writerHolder, writer);
    }

    // Releases _writer. Whoever releases it after the store closed closes the writer connection, so it is closed exactly
    // once, never under a running writer.
    private void ReleaseWriter()
    {
        try
        {
            if (_writerSession is { } stale && !Closed && stale.Statistics != Volatile.Read(ref _statistics))
            {
                _writerSession = null;
                pool.Discard(stale);
            }

            if (Closed && _writerSession is { } session)
            {
                _writerSession = null;
                try
                {
                    // Turso does not checkpoint when the last connection closes: leave the file whole when no reader
                    // stops it (CleanupAsync then deletes the empty log).
                    Scalar(session.Connection, null, "PRAGMA wal_checkpoint(TRUNCATE);");
                }
                catch (SqliteException)
                {
                    // Best effort: the log stays and replays at the next open.
                }

                pool.Discard(session);
            }
        }
        finally
        {
            _writer.Release();
        }
    }

    // The only connection that writes this file: Turso aborts the process when two connections write one file
    // (docs/terrabase/turso-spike.md). The first writer opens it on the write lane, with change capture on, and the store
    // keeps it until it closes. Only the holder of _writer uses it.
    private SqliteSession? _writerSession;

    // Fault probes inspect the session while the transaction still holds the writer.
    internal SqliteSession? CurrentWriter() => _writerSession;

    /// <summary>
    /// The writer connection; the caller holds the writer. Every change it commits is captured in <c>turso_cdc</c>, mode
    /// <c>after</c> (docs/terrabase/multi-region-replication.md section 7), unless a transaction turns capture off with
    /// <see cref="SqliteStorageTransaction.SuspendCaptureAsync"/>.
    /// </summary>
    private async Task<SqliteSession> WriterSessionAsync(CancellationToken cancellationToken)
    {
        if (_writerSession is { } open)
        {
            return open;
        }

        pool.OpenNew(this);
        SqliteSession session;
        try
        {
            var statistics = Volatile.Read(ref _statistics);
            session = await SqliteSession.OpenAsync(WriteLane, _connectionString, pragmas, cancellationToken).ConfigureAwait(false);
            session.Statistics = statistics;
        }
        catch (Exception error)
        {
            pool.OpenFailed();
            if (Closed && !cancellationToken.IsCancellationRequested)
            {
                throw new ObjectDisposedException(GetType().FullName, error);
            }

            throw;
        }

        session = await WriteLane.RunAsync(() => pool.Adopt(this, session), CancellationToken.None).ConfigureAwait(false);
        try
        {
            // Takes the write lock without waiting on busy_timeout; ExecuteAsync retries it.
            await session.ExecuteAsync(WriteLane, () => Scalar(session.Connection, null, SqliteSession.CaptureOn), cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await WriteLane.RunAsync(() => pool.Discard(session), CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        return _writerSession = session;
    }

    /// <summary>Releases what <see cref="BeginTransactionAsync"/> took; called once per transaction.</summary>
    internal void End(bool write, bool ownsAdmission)
    {
        try
        {
            if (write)
            {
                // Closing a stale writer connection can throw; the admission is released all the same.
                ReleaseWriter();
            }
        }
        finally
        {
            if (ownsAdmission)
            {
                io.Release();
            }
        }
    }

    /// <summary>Writes a consistent snapshot of this database to a new file at <paramref name="path"/>; the caller holds an admission.</summary>
    internal async Task CopyToAsync(string path, CancellationToken cancellationToken)
    {
        // Row by row in one read transaction (TursoCopy): writes go on meanwhile.
        await io.RunAsync(() =>
        {
            using var source = new Turso.TursoConnection($"Data Source={FilePath}");
            SqliteSession.OpenExisting(source, FilePath);
            using var begin = source.CreateCommand();
            begin.CommandText = "BEGIN;";
            begin.ExecuteNonQuery();
            TursoCopy.Write(source, path, cancellationToken);
        }, cancellationToken).ConfigureAwait(false);

        // InitializeAsync switches the copy to WAL and refuses an unsupported schema version.
        await InitializeAsync(name, path, io, pragmas, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The database file.</summary>
    internal string FilePath => path;

    private long _backupAnchor = -1;

    /// <summary>
    /// The change id continuous backup's tap read up to, or null while no tap reads this file: CDC retention keeps the rows
    /// from it on (<see cref="SqliteStorageTransaction.RecordAckedAsync"/>).
    /// </summary>
    internal long? BackupAnchor
    {
        get => Volatile.Read(ref _backupAnchor) is >= 0 and var anchor ? anchor : null;
        set => Volatile.Write(ref _backupAnchor, value ?? -1);
    }

    /// <summary>
    /// Runs <paramref name="call"/> while no write transaction of this process is open on this database; writers queue
    /// meanwhile. Continuous backup reads the log and checkpoints it under this (TS-633).
    /// </summary>
    internal async Task<T> WithWritersHeldAsync<T>(Func<Task<T>> call, CancellationToken cancellationToken, string holder = "the continuous backup")
    {
        await AcquireWriterAsync(holder, cancellationToken).ConfigureAwait(false);
        try
        {
            return await call().ConfigureAwait(false);
        }
        finally
        {
            ReleaseWriter();
        }
    }

    /// <summary>
    /// Runs <paramref name="call"/> on the writer connection, on the write lane, while no write transaction is open; for
    /// work that writes outside a transaction or must see every commit (a snapshot, a copy, change-capture cleanup).
    /// </summary>
    internal Task<T> WithWriterAsync<T>(Func<SqliteSession, T> call, string holder, CancellationToken cancellationToken) =>
        WithWritersHeldAsync(() => OnWriterAsync(call, cancellationToken), cancellationToken, holder);

    /// <summary>Runs <paramref name="call"/> on the writer connection; the caller holds the writer (<see cref="WithWritersHeldAsync"/>).</summary>
    internal async Task<T> OnWriterAsync<T>(Func<SqliteSession, T> call, CancellationToken cancellationToken)
    {
        var writer = await WriterSessionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await writer.ExecuteAsync(WriteLane, () => call(writer), cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException)
        {
            // The connection may be left mid-statement: the next writer opens a fresh one.
            await WriteLane.RunAsync(() => Discard(writer), CancellationToken.None).ConfigureAwait(false);
            throw;
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

    /// <summary>Runs once a group commit batch committed, before its transaction is disposed; integration tests fail it.</summary>
    internal Action? AfterGroupCommit { get; set; }

    /// <summary>Runs inside a transaction's CommitAsync right after its SQLite commit succeeded; integration tests fail it.</summary>
    internal Action? AfterSqliteCommit { get; set; }

    /// <summary>The generation <see cref="StatisticsChanged"/> moves, for tests.</summary>
    internal int Statistics => Volatile.Read(ref _statistics);

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
        SqliteStorageTransaction? transaction = null;
        try
        {
            transaction = await BeginAsync(write: true, capture: true, durable: false, CancellationToken.None, "a group commit batch").ConfigureAwait(false);
            await using (transaction.ConfigureAwait(false))
            {
                for (var index = 0; index < batch.Count; index++)
                {
                    var write = batch[index];
                    if (write.Cancelled)
                    {
                        write.Cancel();
                        continue;
                    }

                    transaction.Savepoint();
                    var keeps = false;
                    Exception? failure = null;
                    try
                    {
                        keeps = await write.RunAsync(transaction).ConfigureAwait(false);
                        if (keeps)
                        {
                            // The write's documents get their one state row each before the next write can touch them.
                            await transaction.FlushStatesAsync().ConfigureAwait(false);
                        }
                    }
                    catch (Exception error)
                    {
                        failure = error;
                    }

                    if (transaction.Aborted)
                    {
                        // Turso rolled the whole transaction back with this write's failed statement (a stopped read,
                        // even one the write answered itself): only this write is answered, and the writes the rollback
                        // took with it lead the next batch.
                        deferred = [.. kept, .. refused, .. deferred, .. batch.Skip(index + 1)];
                        kept.Clear();
                        refused.Clear();
                        if (failure is null)
                        {
                            refused.Add(write);
                        }
                        else
                        {
                            write.Fail(failure);
                        }

                        break;
                    }

                    if (failure is not null)
                    {
                        await transaction.RollbackToSavepointAsync().ConfigureAwait(false);
                        if (transaction.LimitsReached && kept.Count > 0)
                        {
                            deferred.Add(write);
                        }
                        else
                        {
                            write.Fail(failure);
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
                    AfterGroupCommit?.Invoke();
                }
            }
        }
        catch (Exception error) when (!IsSystem && transaction?.Committed is { } committedAt && !transaction.ChangedReaderState)
        {
            // A client database's batch committed and changed no state readers see beside its documents: its writes are
            // answered as committed, not as a failure a client would retry. What failed is a wake-up or the cleanup after
            // the commit (disposing the transaction, releasing the writer).
            offset = committedAt;
            try
            {
                logger?.ZLogError(error, $"A group commit of database {name:@Database} committed, but the work after its commit failed; its writes are answered as committed");
            }
            catch (Exception)
            {
                // A failing logger must not fault the batch loop, which would leave this database's writes waiting forever.
            }
        }
        catch (Exception error)
        {
            // Nothing of this batch committed, or it is the catalog's, which fails closed: every write not answered yet
            // fails with it.
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

    /// <summary>Takes back a session whose work has ended; the pool keeps it idle or closes it.</summary>
    /// <remarks>Call on an I/O thread: an eviction can checkpoint the WAL of another database.</remarks>
    internal void Return(SqliteSession session)
    {
        // The writer connection stays the store's; ReleaseWriter closes it once the store closed.
        if (session == _writerSession)
        {
            return;
        }

        if (session.Statistics != Volatile.Read(ref _statistics))
        {
            pool.Discard(session);
            return;
        }

        pool.Return(this, session);
    }

    // Counts index builds: Turso reads the planner's statistics (sqlite_stat1) only when a connection opens, so a session
    // opened before the last ANALYZE plans without its index's statistics and is closed instead of pooled.
    private int _statistics;

    /// <summary>Called by a commit that built an index; older sessions, the writer included, are closed when they end.</summary>
    internal void StatisticsChanged() => Interlocked.Increment(ref _statistics);

    /// <summary>Closes a failed rented session, or the writer connection; call only on an I/O worker.</summary>
    internal void Discard(SqliteSession session)
    {
        if (session == _writerSession)
        {
            _writerSession = null;
        }

        pool.Discard(session);
    }

    private Task ReturnAsync(SqliteSession session) => io.RunAsync(() => Return(session), CancellationToken.None);

    /// <summary>
    /// Closes idle sessions and refuses new ones; a rented session is closed when it is returned.
    /// The engine calls this when the database is deleted and at shutdown.
    /// </summary>
    public void Dispose()
    {
        pool.Close(this);
        // A writer holding _writer now closes the connection itself when it releases it.
        if (_writer.Wait(0))
        {
            ReleaseWriter();
        }
    }

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
            var statistics = Volatile.Read(ref _statistics);
            session = await SqliteSession.OpenAsync(runner, _connectionString, pragmas, cancellationToken).ConfigureAwait(false);
            session.Statistics = statistics;
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
        string name, string path, SqliteIoPool io, string pragmas, CancellationToken cancellationToken) =>
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
                // The creating connection uses the same settings as every pooled one, except that it does not sync: the
                // file is private until it is published, and synced once then. Turso's journal is WAL by default.
                Scalar(connection, null, pragmas + "PRAGMA synchronous = OFF;");
                Bootstrap(name, connection, cancellationToken);
                // Publish a fresh identity, on this file only.
                using var transaction = connection.BeginTransaction(deferred: false);
                RenewIncarnation(connection, transaction);
                cancellationToken.ThrowIfCancellationRequested();
                transaction.Commit();
                // Turso does not checkpoint when the last connection closes: copy the log into the file, which is moved
                // without its sidecars.
                Scalar(connection, null, "PRAGMA wal_checkpoint(TRUNCATE);");
            }

            // The file alone now holds the database: sync it once, before it can be published.
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
        var schema = name == SqliteStorageEngine.SystemName ? SystemSchemaSql : ClientSchemaSql;
        Scalar(connection, transaction, schema + UniqueSchemaSql + $"PRAGMA user_version = {SchemaVersion};");
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

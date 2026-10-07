using System.Collections.Frozen;
using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;
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
internal sealed class SqliteDatabaseStore(
    string name, string path, SqliteSessionPool pool, SqliteIoPool io, string pragmas, TenantContext? tenant, TimeProvider? time,
    Action<long>? changesCommitted, Action? committed) : IDatabaseStore, IDisposable
{
    internal SqliteDatabaseStore(string name, string path, SqliteSessionPool pool, SqliteIoPool io, string pragmas, TimeProvider? time = null, Action? committed = null)
        : this(name, path, pool, io, pragmas, null, time, null, committed) { }

    /// <summary>On-disk schema version, stored in <c>PRAGMA user_version</c>.</summary>
    private const int SchemaVersion = 8;

    // Migrations[n] moves user_version n to n + 1.
    private static readonly string[] Migrations =
    [
        """
        CREATE TABLE IF NOT EXISTS documents (
            collection TEXT NOT NULL,
            id TEXT NOT NULL,
            version INTEGER NOT NULL,
            body TEXT NOT NULL,
            PRIMARY KEY (collection, id)
        ) WITHOUT ROWID;
        """,
        // Version 2: explicit collections, tombstones (body NULL) and a per-database commit offset.
        // A document's version becomes the offset of its last write; offsets continue after every
        // version 1 counter so no document ever sees a version again.
        """
        CREATE TABLE collections (name TEXT PRIMARY KEY, created_at INTEGER NOT NULL) WITHOUT ROWID;
        CREATE TABLE commit_offset (id INTEGER PRIMARY KEY CHECK (id = 1), value INTEGER NOT NULL);
        CREATE TABLE documents_v2 (
            collection TEXT NOT NULL,
            id TEXT NOT NULL,
            version INTEGER NOT NULL,
            body TEXT,
            PRIMARY KEY (collection, id)
        ) WITHOUT ROWID;
        INSERT INTO documents_v2 (collection, id, version, body) SELECT collection, id, version, body FROM documents;
        DROP TABLE documents;
        ALTER TABLE documents_v2 RENAME TO documents;
        INSERT INTO collections (name, created_at) SELECT DISTINCT collection, CAST(unixepoch('subsec') * 1000 AS INTEGER) FROM documents;
        INSERT INTO commit_offset (id, value) SELECT 1, coalesce(max(version), 0) FROM documents;
        """,
        // Version 3 also upgrades databases opened by an earlier build of the document API.
        "CREATE INDEX documents_live ON documents (collection, id) WHERE body IS NOT NULL;",
        // Version 4: offsets can restart after database recreation; versions also need a persisted incarnation.
        """
        ALTER TABLE commit_offset ADD COLUMN incarnation TEXT NOT NULL DEFAULT '';
        """,
        // Version 5: shape history. Every write is retained in change_log so shape snapshots at an
        // offset and closed log chunks never change (docs/shape-reads.md). Collections get a random
        // generation per creation. Existing files start history at their head, with their current
        // rows as the baseline: older offsets are below the floor and answer 409 must_refetch.
        """
        ALTER TABLE collections ADD COLUMN generation INTEGER NOT NULL DEFAULT 0;
        UPDATE collections SET generation = random();
        CREATE TABLE shape_history (id INTEGER PRIMARY KEY CHECK (id = 1), floor INTEGER NOT NULL);
        INSERT INTO shape_history (id, floor) SELECT 1, value FROM commit_offset;
        CREATE TABLE change_log (
            sequence INTEGER PRIMARY KEY,
            collection TEXT NOT NULL,
            generation INTEGER NOT NULL,
            id TEXT NOT NULL,
            version INTEGER NOT NULL,
            body TEXT
        );
        CREATE INDEX change_log_by_offset ON change_log (collection, generation, version, sequence);
        CREATE INDEX change_log_by_document ON change_log (collection, generation, id, version, sequence);
        INSERT INTO collections (name, created_at, generation)
            SELECT collection, CAST(unixepoch('subsec') * 1000 AS INTEGER), random() FROM documents
            WHERE collection NOT IN (SELECT name FROM collections) GROUP BY collection;
        """,
        // Version 6: each document's first write per collection generation, so a snapshot finds the ids that
        // existed at its offset without reading the revisions of later ones, and the generation's highest id
        // over time (a new row each time a new id sorts above every earlier one), so a snapshot stops at the
        // highest id at its offset and never walks ids appended later, such as UUIDv7s. Existing history is
        // backfilled with each id's earliest retained version; the trigger keeps every later write. Offsets
        // grow in commit order, so the highest id at an offset is that of its latest row at or before it.
        """
        CREATE TABLE change_log_births (
            collection TEXT NOT NULL,
            generation INTEGER NOT NULL,
            id TEXT NOT NULL,
            version INTEGER NOT NULL,
            PRIMARY KEY (collection, generation, id, version)
        ) WITHOUT ROWID;
        INSERT INTO change_log_births (collection, generation, id, version)
            SELECT collection, generation, id, min(version) FROM change_log GROUP BY collection, generation, id;
        CREATE TABLE change_log_high (
            collection TEXT NOT NULL,
            generation INTEGER NOT NULL,
            version INTEGER NOT NULL,
            id TEXT NOT NULL,
            PRIMARY KEY (collection, generation, version, id)
        ) WITHOUT ROWID;
        INSERT INTO change_log_high (collection, generation, version, id)
            SELECT collection, generation, version, id FROM (
                SELECT collection, generation, version, id,
                    max(id) OVER (PARTITION BY collection, generation ORDER BY version, id ROWS UNBOUNDED PRECEDING) AS high
                FROM change_log_births)
            WHERE id = high;
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
        """,
        // Version 7: capture and applied frontiers use the existing incarnation in commit_offset.
        """
        ALTER TABLE change_log ADD COLUMN document_version INTEGER NOT NULL DEFAULT 0;
        UPDATE change_log SET document_version = version;
        CREATE TABLE changes (
            sequence INTEGER NOT NULL,
            ordinal INTEGER NOT NULL,
            collection TEXT NOT NULL,
            id TEXT NOT NULL,
            version INTEGER NOT NULL,
            body TEXT,
            PRIMARY KEY (sequence, ordinal)
        ) WITHOUT ROWID;
        CREATE INDEX changes_deleted ON changes (collection, id, version) WHERE body IS NULL;
        CREATE TABLE replication_state (id INTEGER PRIMARY KEY CHECK (id = 1), complete INTEGER NOT NULL);
        INSERT INTO replication_state (id, complete) SELECT 1, NOT EXISTS (SELECT 1 FROM documents);
        CREATE TABLE applied (origin BLOB PRIMARY KEY, sequence INTEGER NOT NULL) WITHOUT ROWID;
        """,
        // Version 8: a sequence alone cannot identify a prefix after snapshot restoration and replacement writes.
        // Legacy applied prefixes have no content proof: replay them rather than trusting an ambiguous acknowledgment.
        """
        CREATE TABLE journal_prefixes (
            sequence INTEGER PRIMARY KEY CHECK (sequence > 0),
            digest BLOB NOT NULL CHECK (length(digest) = 32)
        ) WITHOUT ROWID;
        ALTER TABLE applied ADD COLUMN digest BLOB CHECK (digest IS NULL OR length(digest) = 32);
        DELETE FROM applied;
        """,
    ];

    // Version 5's history baseline, for client databases only: _system keeps no shape history, so its
    // existing records (invitations, audit events) must not be copied where its deletes never reach.
    private const string ShapeBaseline = """
        INSERT INTO change_log (collection, generation, id, version, body)
            SELECT d.collection, c.generation, d.id, d.version, d.body FROM documents d JOIN collections c ON c.name = d.collection
            ORDER BY d.version, d.collection, d.id;
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

    /// <summary>The thread this database's write transactions run on, apart from the read pool.</summary>
    internal SqliteWriteLane WriteLane { get; } = new(name);

    // Boxed so a catalog follower can adopt its primary's incarnation without torn reads.
    private volatile StrongBox<Guid> _incarnation = new(Guid.Empty);

    public Guid Incarnation => _incarnation.Value;

    /// <summary>Publishes the incarnation a catalog follower's committed snapshot adopted (docs/decisions/0009).</summary>
    internal void AdoptIncarnation(Guid incarnation) => _incarnation = new(incarnation);

    public TenantContext? Tenant { get; } = tenant;

    // Client databases scope ETags by their ids, which every member shares, so a version read on one member matches on
    // another (TS-574); _system uses its incarnation, which catalog followers adopt from their primary.
    private readonly Guid? _scope = tenant?.VersionScope();

    /// <summary>The <see cref="IStorageTransaction.Scope"/> of this database's transactions.</summary>
    internal Guid Scope => _scope ?? Incarnation;

    /// <summary>Set once the database is deleted or the engine shuts down; guarded by <see cref="SqliteSessionPool"/>.</summary>
    internal bool Closed
    {
        get => Volatile.Read(ref field);
        set => Volatile.Write(ref field, value);
    }

    public bool IsClosed => Closed;

    internal TimeProvider Time => time ?? TimeProvider.System;

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
        Replica ? throw new CatalogFollowerException() : await BeginAsync(write: true, capture: true, durable: true, cancellationToken).ConfigureAwait(false);

    /// <summary>The write transaction of a follower's replication applier; it captures nothing.</summary>
    internal ValueTask<SqliteStorageTransaction> BeginReplicaAsync(CancellationToken cancellationToken) =>
        BeginAsync(write: true, capture: false, durable: false, cancellationToken);

    private async ValueTask<SqliteStorageTransaction> BeginAsync(bool write, bool capture, bool durable, CancellationToken cancellationToken)
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
                // The same 30 s bound as busy_timeout, which this wait replaces in-process.
                if (!await _writer.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false))
                {
                    throw io.Busy();
                }

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
        if (!await _writer.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false))
        {
            throw io.Busy();
        }

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
                _ = Task.Run(RunBatchesAsync, CancellationToken.None);
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
            }

            // The queued writes' admissions cover the batch's transaction.
            deferred = await io.WithAdmissionAsync(() => CommitBatchAsync(batch)).ConfigureAwait(false);
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
            var transaction = await BeginAsync(write: true, capture: true, durable: false, CancellationToken.None).ConfigureAwait(false);
            await using (transaction.ConfigureAwait(false))
            {
                foreach (var write in batch)
                {
                    if (write.Cancelled)
                    {
                        write.Cancel();
                        continue;
                    }

                    await transaction.SavepointAsync().ConfigureAwait(false);
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
                        await transaction.ReleaseSavepointAsync().ConfigureAwait(false);
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

        public Task<(T Result, long Offset)> Task => _done.Task;

        public bool Cancelled => cancellationToken.IsCancellationRequested;

        public async ValueTask<bool> RunAsync(IStorageTransaction transaction)
        {
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
    /// When the change log does not cover this database's state (a copy, or an upgrade from version 1 with
    /// data), appends the current state of every document and tombstone as baseline change groups, one
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
    /// Creates (or adopts a copied) and migrates a new database file at <paramref name="path"/> on an unpooled
    /// connection, so no handle stays open once this returns and the file can be moved. The caller holds an admission.
    /// </summary>
    internal static Task InitializeAsync(
        string name, string path, SqliteIoPool io, string pragmas, string? afterMigration, CancellationToken cancellationToken) =>
        io.RunAsync(() =>
        {
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false,
            }.ToString();
            using var connection = new SqliteConnection(connectionString);
            connection.Open();
            // A copy made by VACUUM INTO may already be current, so set WAL here and not only in a migration.
            // The creating connection uses the same settings as every pooled one.
            Scalar(connection, null, "PRAGMA journal_mode = WAL;" + pragmas);
            Migrate(name, connection, cancellationToken);
            // Publish a fresh identity and prepare the copied baseline atomically, on this file only.
            using var transaction = connection.BeginTransaction(deferred: false);
            RenewIncarnation(connection, transaction);
            if (afterMigration is not null)
            {
                Scalar(connection, transaction, afterMigration);
            }

            cancellationToken.ThrowIfCancellationRequested();
            transaction.Commit();
            return true;
        }, cancellationToken);

    // The caller holds an admission.
    internal async Task MigrateAsync(CancellationToken cancellationToken)
    {
        var session = await RentAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await session.ExecuteAsync(io, () =>
            {
                // A migration is atomic and rolls back before a lock error leaves this scope.
                Migrate(name, session.Connection, cancellationToken);
                _incarnation = new(Guid.ParseExact(
                    (string)(Scalar(session.Connection, null, SqliteStatements.Incarnation)
                        ?? throw new InvalidOperationException("database incarnation is missing")), "N"));
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

    private static void Migrate(string name, SqliteConnection connection, CancellationToken cancellationToken)
    {
        if (Version(name, connection, transaction: null) == SchemaVersion)
        {
            return;
        }

        // journal_mode cannot change inside a transaction; it is persistent and idempotent.
        Scalar(connection, null, "PRAGMA journal_mode = WAL;");
        using var transaction = connection.BeginTransaction(deferred: false);
        // Another process or caller may have migrated while this one waited for the lock.
        var version = Version(name, connection, transaction);
        for (; version < SchemaVersion; version++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Scalar(connection, transaction, Migrations[version] + $"PRAGMA user_version = {version + 1};");
            if (version == 3)
            {
                RenewIncarnation(connection, transaction);
            }

            if (version == 4 && name != SqliteStorageEngine.SystemName)
            {
                Scalar(connection, transaction, ShapeBaseline);
            }

            if (version == 7)
            {
                BackfillJournalDigests(connection, transaction, cancellationToken);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        transaction.Commit();
    }

    // The migration retains at most one 1024-item/2-MiB group, never the whole journal. The prefix chain
    // and user_version publish together, so interruption cannot leave a partially trusted journal.
    private static void BackfillJournalDigests(SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        var origin = (byte[])(Scalar(connection, transaction, SqliteStatements.Origin)
            ?? throw new InvalidDataException("the journal origin is missing"));
        var previous = JournalDigest.Initial(origin);
        var head = (long)Scalar(connection, transaction, SqliteStatements.LastSequence)!;
        using var read = connection.CreateCommand();
        using var append = connection.CreateCommand();
        read.Transaction = append.Transaction = transaction;
        read.CommandText = SqliteStatements.ReadChanges;
        read.Parameters.AddWithValue("$sequence", 0L);
        read.Parameters.AddWithValue("$limit", IStorageTransaction.MaxChangeGroupChanges + 1);
        read.Parameters.AddWithValue("$maxBytes", IStorageTransaction.MaxChangeGroupBytes);
        append.CommandText = SqliteStatements.AppendChangeDigest;
        append.Parameters.AddWithValue("$sequence", 0L);
        append.Parameters.AddWithValue("$digest", previous);
        for (var sequence = 1L; sequence <= head;)
        {
            cancellationToken.ThrowIfCancellationRequested();
            read.Parameters["$sequence"].Value = sequence;
            var changes = SqliteStorageTransaction.ReadChanges(read);
            if (changes.Count == 0)
            {
                throw new InvalidDataException("the captured journal is not contiguous");
            }

            previous = JournalDigest.Compute(origin, sequence, previous, changes);
            append.Parameters["$sequence"].Value = sequence;
            append.Parameters["$digest"].Value = previous;
            append.ExecuteNonQuery();
            if (sequence == head)
            {
                break;
            }

            sequence++;
        }
    }

    // Returns the schema version; throws for a newer, unsupported schema.
    private static int Version(string name, SqliteConnection connection, SqliteTransaction? transaction)
    {
        var version = Convert.ToInt32(
            Scalar(connection, transaction, "PRAGMA user_version;"),
            System.Globalization.CultureInfo.InvariantCulture);
        return version > SchemaVersion
            ? throw new InvalidOperationException($"database '{name}' has schema version {version}; this build supports up to {SchemaVersion}")
            : version;
    }

    // Called only from the worker that owns this connection, including during migration and copying.
    private static void RenewIncarnation(SqliteConnection connection, SqliteTransaction? transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = SqliteStatements.RenewIncarnation;
        command.Parameters.AddWithValue("$incarnation", Guid.NewGuid().ToString("N"));
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

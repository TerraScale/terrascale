using System.Collections.Frozen;
using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using Turso.Data.Sqlite;
using TerraBase.Configuration;
using TerraBase.Features.Memory;
using TerraBase.Hosting;
using TerraBase.Features.Qos;
using TerraBase.Primitives;
using TerraBase.Tenancy;
using ZLogger;

namespace TerraBase.Engine.Turso;

/// <param name="name">Names the database in error messages: its id, or <c>_system</c>.</param>
/// <param name="path">The database file.</param>
/// <param name="pool">Idle sessions, shared by every database of the engine.</param>
/// <param name="io">The bounded Turso worker pool.</param>
/// <param name="pragmas">Connection pragmas applied to each session.</param>
/// <param name="tenant">The storage identity; null for the system database.</param>
/// <param name="time">The clock of the query deadline.</param>
/// <param name="committed">Optional engine notification after a successful commit that changed this database.</param>
/// <param name="logger">Receives the warning about a long wait for this database's writer.</param>
/// <param name="schemaMemory">Shared process admission for schema evaluation, when hosted.</param>
/// <param name="schemaEvaluations">Host-owned counter of collection schema keyword evaluations.</param>
/// <param name="encryption">Wraps and unwraps this database's data key.</param>
/// <param name="storageReads">Host-owned counter of physical document and feed SELECTs, tagged by path (point, query, feed, shape).</param>
internal sealed partial class TursoDatabaseStore(
    string name, string path, TursoSessionPool pool, TursoIoPool io, string pragmas, TenantContext? tenant, TimeProvider? time,
    Action? committed, ILogger? logger = null, IMemoryBudget? schemaMemory = null, Counter<long>? schemaEvaluations = null,
    Counter<long>? storageReads = null, DatabaseKeys? encryption = null) : IDatabaseStore, IDisposable
{
    internal Action? ReplicationCommitted { get; init; }
    internal DatabaseKeys Encryption { get; } = encryption ?? throw new ArgumentNullException(nameof(encryption));

    internal IMemoryBudget? SchemaMemory { get; } = schemaMemory;
    internal Action<string>? CollectionStateWritten { get; init; }
    internal Counter<long>? SchemaEvaluations { get; } = schemaEvaluations;
    internal Counter<long>? StorageReads { get; } = storageReads;

    internal TursoDatabaseStore(string name, string path, TursoSessionPool pool, TursoIoPool io, string pragmas, DatabaseKeys encryption, TimeProvider? time = null, Action? committed = null)
        : this(name, path, pool, io, pragmas, null, time, committed, encryption: encryption) { }

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
    // (docs/terrabase/turso-spike.md). The system database is a lattice database like a client one (section 15 of
    // docs/terrabase/multi-region-replication.md): each document's DocumentState in _tb_state and its materialized JSON
    // beside it, captured and shipped by the same CDC tailer and peer streams. Its collections are paths
    // (projects/<id>/databases), one set per tenant, so its live documents share one documents table instead of a table
    // per collection. collections and commit_offset are the engine tables both kinds share. _tb_rebuild and
    // _tb_write_intent are this node's copy faults and rebuilds (RebuildLedger): node-local, written uncaptured, never a
    // document state, so no peer receives them.
    private const string SystemSchemaSql = """
        CREATE TABLE documents (collection TEXT NOT NULL, id TEXT NOT NULL, body TEXT NOT NULL, UNIQUE (collection, id));
        CREATE TABLE _tb_state (
            collection TEXT NOT NULL, id TEXT NOT NULL, bucket INTEGER NOT NULL, state TEXT NOT NULL,
            stamp INTEGER NOT NULL, origin INTEGER NOT NULL, live INTEGER NOT NULL, h1 INTEGER NOT NULL, h2 INTEGER NOT NULL,
            UNIQUE (collection, id)
        );
        CREATE INDEX _tb_state_bucket ON _tb_state (bucket, collection, id);
        CREATE INDEX _tb_state_dead ON _tb_state (stamp) WHERE live = 0;
        CREATE TABLE _tb_merkle (bucket INTEGER PRIMARY KEY, h1 INTEGER NOT NULL, h2 INTEGER NOT NULL);
        CREATE TABLE _tb_gc_floor (id INTEGER PRIMARY KEY CHECK (id = 1), stamp INTEGER NOT NULL);
        INSERT INTO _tb_gc_floor (id, stamp) VALUES (1, 0);
        CREATE TABLE _tb_peers (
            node INTEGER PRIMARY KEY, applied INTEGER NOT NULL, acked INTEGER NOT NULL, stable INTEGER NOT NULL, seen INTEGER NOT NULL,
            incarnation TEXT NOT NULL DEFAULT ''
        );
        CREATE TABLE collections (
            name TEXT PRIMARY KEY, created_at INTEGER NOT NULL,
            entity_id TEXT NOT NULL CHECK (length(entity_id) = 26)
        );
        CREATE TABLE commit_offset (
            id INTEGER PRIMARY KEY CHECK (id = 1), value INTEGER NOT NULL, incarnation TEXT NOT NULL
        );
        INSERT INTO commit_offset (id, value, incarnation) VALUES (1, 0, '');
        CREATE TABLE _tb_rebuild (
            organization TEXT NOT NULL, project TEXT NOT NULL, database TEXT NOT NULL, member INTEGER NOT NULL,
            old_incarnation TEXT NOT NULL, phase INTEGER NOT NULL, new_incarnation TEXT, reason TEXT NOT NULL CHECK (length(reason) <= 256),
            source_machine TEXT CHECK (length(source_machine) <= 255), source_incarnation TEXT, source_head INTEGER, additional_incarnation TEXT,
            UNIQUE (organization, project, database)
        );
        CREATE TABLE _tb_write_intent (
            organization TEXT NOT NULL, project TEXT NOT NULL, database TEXT NOT NULL, incarnation TEXT NOT NULL, replica INTEGER NOT NULL,
            UNIQUE (organization, project, database)
        );
        """;

    // A client database (docs/terrabase/multi-region-replication.md section 5). Each collection is a table of its own, created
    // with it (the metadata collections here), holding the materialized JSON of its live documents (doc): reads, queries,
    // and indexes never decode a state. _tb_state keeps each document's replicated DocumentState in its
    // stored form (base64url), with its anti-entropy bucket and digest; the other _tb_ tables belong to the change feed,
    // the peer streams and anti-entropy, which later slices fill; _tb_refill holds the certificate of a rebuild's refill
    // (decision 0013a section 1). _tb_collection_stats keeps each client collection's live document count and bytes, written
    // with its rows, so usage and storage reads never scan a collection. Every new table is a rowid table.
    private const string ClientSchemaSql = """
        CREATE TABLE "_collections" (id TEXT NOT NULL UNIQUE, doc TEXT NOT NULL);
        CREATE TABLE "_indexes" (id TEXT NOT NULL UNIQUE, doc TEXT NOT NULL);
        CREATE TABLE _tb_text_indexes (name TEXT PRIMARY KEY, collection TEXT NOT NULL, fields TEXT NOT NULL);
        CREATE TABLE _tb_vector_indexes (name TEXT PRIMARY KEY, collection TEXT NOT NULL, field TEXT NOT NULL, dimensions INTEGER NOT NULL, built TEXT);
        CREATE TABLE "_unique" (id TEXT NOT NULL UNIQUE, doc TEXT NOT NULL);
        CREATE TABLE _tb_state (
            collection TEXT NOT NULL, id TEXT NOT NULL, bucket INTEGER NOT NULL, state TEXT NOT NULL,
            stamp INTEGER NOT NULL, origin INTEGER NOT NULL, live INTEGER NOT NULL, h1 INTEGER NOT NULL, h2 INTEGER NOT NULL,
            UNIQUE (collection, id)
        );
        CREATE INDEX _tb_state_bucket ON _tb_state (bucket, collection, id);
        CREATE INDEX _tb_state_dead ON _tb_state (stamp) WHERE live = 0;
        CREATE TABLE _tb_gc_floor (id INTEGER PRIMARY KEY CHECK (id = 1), stamp INTEGER NOT NULL);
        INSERT INTO _tb_gc_floor (id, stamp) VALUES (1, 0);
        CREATE TABLE _tb_peers (
            node INTEGER PRIMARY KEY, applied INTEGER NOT NULL, acked INTEGER NOT NULL, stable INTEGER NOT NULL, seen INTEGER NOT NULL,
            incarnation TEXT NOT NULL DEFAULT ''
        );
        CREATE TABLE _tb_feed (
            seq INTEGER NOT NULL UNIQUE, commit_offset INTEGER NOT NULL, origin INTEGER NOT NULL, stamp INTEGER NOT NULL,
            collection TEXT NOT NULL, id TEXT NOT NULL, deleted INTEGER NOT NULL, prefix BLOB NOT NULL,
            access_before TEXT, access_after TEXT, collection_generation TEXT
        );
        CREATE INDEX _tb_feed_order ON _tb_feed (origin, stamp);
        CREATE TABLE _tb_feed_sequence (id INTEGER PRIMARY KEY CHECK (id = 1), seq INTEGER NOT NULL);
        INSERT INTO _tb_feed_sequence (id, seq) VALUES (1, 0);
        CREATE TABLE _tb_feed_floor (origin INTEGER PRIMARY KEY, stamp INTEGER NOT NULL, prefix BLOB NOT NULL);
        CREATE TABLE _tb_shape_chunk (
            handle TEXT NOT NULL, offset INTEGER NOT NULL, end_offset INTEGER NOT NULL,
            body BLOB NOT NULL, sealed_at INTEGER NOT NULL, UNIQUE (handle, offset)
        );
        CREATE TABLE _tb_shape_snapshot (
            handle TEXT NOT NULL, offset INTEGER NOT NULL, page INTEGER NOT NULL, next_id TEXT,
            body BLOB NOT NULL, sealed_at INTEGER NOT NULL, UNIQUE (handle, offset, page)
        );
        CREATE TABLE _tb_merkle (bucket INTEGER PRIMARY KEY, h1 INTEGER NOT NULL, h2 INTEGER NOT NULL);
        CREATE TABLE _tb_tail (id INTEGER PRIMARY KEY CHECK (id = 1), change_id INTEGER NOT NULL);
        CREATE TABLE _tb_refill (origin TEXT PRIMARY KEY, incarnation TEXT NOT NULL, head INTEGER NOT NULL);
        CREATE TABLE collections (
            name TEXT PRIMARY KEY, created_at INTEGER NOT NULL,
            entity_id TEXT NOT NULL CHECK (length(entity_id) = 26)
        );
        CREATE TABLE _tb_collection_stats (collection TEXT PRIMARY KEY, documents INTEGER NOT NULL, bytes INTEGER NOT NULL);
        CREATE TABLE commit_offset (
            id INTEGER PRIMARY KEY CHECK (id = 1), value INTEGER NOT NULL, incarnation TEXT NOT NULL
        );
        INSERT INTO commit_offset (id, value, incarnation) VALUES (1, 0, '');
        """;

    // Keys are unwrapped per native open; no extra process-wide cache of plaintext database keys.
    internal Task<string> ConnectionStringAsync(TursoRunner runner, CancellationToken cancellationToken) =>
        Encryption.ExistingAsync(FilePath, runner, cancellationToken);

    // Signals every write commit, collection-only ones included, to WaitForCommitAsync.
    private readonly Lock _commits = new();
    private long _sequence;
    private TaskCompletionSource _signal = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Whether this is TerraBase's own database rather than a client database.</summary>
    internal bool IsSystem => name == TursoStorageEngine.SystemName;

    // The node-local write-intent journal is never replicated or backed up: its writer captures nothing, so no transaction
    // pays to turn capture off and on again (two toggles per write before, one recording and one retiring the intent).
    // Known by its file: the journal's store is named SYSTEM for SYSTEM's semantics (TursoStorageEngine.Intents.cs).
    internal bool Captures { get; } = !string.Equals(Path.GetFileName(path), TursoStorageEngine.IntentFile, StringComparison.Ordinal);

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

    // Writers queue here, asynchronously, rather than in BEGIN IMMEDIATE: writers blocked in Turso's
    // busy handler would hold every I/O thread while the writer that holds the lock waits for one.
    private readonly SemaphoreSlim _writer = new(1, 1);

    /// <summary>A wait for the writer this long or longer logs who held it (<see cref="AcquireWriterAsync"/>).</summary>
    internal static readonly TimeSpan SlowWriterWait = TimeSpan.FromSeconds(5);

    /// <summary>A hold this long or longer logs its holder and where it began, whether or not anything waited for it.</summary>
    internal static readonly TimeSpan LongWriterHold = TimeSpan.FromSeconds(1);

    // What holds _writer and since when (a TimeProvider timestamp), set by each holder once it took it. Read without the
    // gate, for the warning only.
    private string? _writerHolder;
    private long _writerTaken;

    // Where the holder began its transaction (caller file and line): a "between storage calls" hold names its caller.
    private string? _writerFile;
    private int _writerLine;

    // Set by a holder that took the writer through AcquireWriterAsync, cleared when it releases it: what WriterHeld records.
    private long _holdStarted;

    /// <summary>Records how long each holder kept the writer, by holder (a bounded set of names); null outside an engine.</summary>
    internal Histogram<double>? WriterHeld { get; init; }

    /// <summary>Counts begun transactions by kind and call site; null outside an engine.</summary>
    internal Counter<long>? Transactions { get; init; }

    /// <summary>The thread this database's write transactions run on, apart from the read pool.</summary>
    internal TursoWriteLane WriteLane { get; } = new(name, io);

    internal TursoRevisionLedger Revisions { get; } = new(path, schemaMemory, tenant);

    // Boxed so a reader never sees a torn value.
    private volatile StrongBox<Ulid> _incarnation = new(Ulid.Empty);

    public Ulid Incarnation => _incarnation.Value;

    public TenantContext? Tenant { get; } = tenant;

    // ETags are scoped by the database's ids, which every member shares, so a version read on one member matches on
    // another (TS-574); _system is the same database on every member.
    private static readonly Guid SystemScope = new TenantContext(TursoStorageEngine.SystemName, PlanTier.Hobby).VersionScope();

    /// <summary>The <see cref="IStorageTransaction.Scope"/> of this database's transactions.</summary>
    internal Guid Scope { get; } = tenant?.VersionScope() ?? SystemScope;

    /// <summary>Set once the database is deleted or the engine shuts down; guarded by <see cref="TursoSessionPool"/>.</summary>
    internal bool Closed
    {
        get => Volatile.Read(ref field);
        set => Volatile.Write(ref field, value);
    }

    public bool IsClosed => Closed;

    internal TimeProvider Time => time ?? TimeProvider.System;

    // Invalidates baselines staged while another committed transaction discarded deletion evidence. Unlike the floor,
    // this also changes when a newly received old tombstone is collected below an already inherited floor.
    private long _collectionGeneration;
    internal long CollectionGeneration => Volatile.Read(ref _collectionGeneration);
    internal void Collected() => Interlocked.Increment(ref _collectionGeneration);

    internal static readonly TimeSpan DefaultQueryBudget = TimeSpan.FromSeconds(TerraBaseOptions.DefaultQueryBudgetSeconds);

    /// <summary>Turso execution time one query may spend on <see cref="Time"/> before it is refused.</summary>
    internal TimeSpan QueryBudget { get; init; } = DefaultQueryBudget;

    /// <summary>Idle sessions of this database, for tests.</summary>
    internal int IdleCount => pool.IdleCountOf(this);

    /// <summary>What this database's local writes mint their stamps with.</summary>
    internal LocalReplica? Member { get; init; }

    internal bool RequiresRemoteProof { get; init; }

    public async ValueTask<IStorageTransaction> BeginTransactionAsync(
        bool write, CancellationToken cancellationToken, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0) =>
        await BeginAsync(write, durable: false, cancellationToken, file: file, line: line).ConfigureAwait(false);

    public async ValueTask<IStorageTransaction> BeginDurableTransactionAsync(
        CancellationToken cancellationToken, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0) =>
        await BeginAsync(write: true, durable: true, cancellationToken, "a durable write transaction", file: file, line: line).ConfigureAwait(false);

    /// <summary>
    /// A write transaction of a rebuild's certified refill, salvage or completion barrier: the only writes a copy under
    /// rebuild accepts. The caller checks the file's incarnation inside it (<see cref="RebuildLedger"/>).
    /// </summary>
    /// <remarks>Only the intent journal's ordinary intents pass <paramref name="synced"/> false (decision 0013d).</remarks>
    internal ValueTask<TursoStorageTransaction> BeginRebuildTransactionAsync(CancellationToken cancellationToken, bool synced = true) =>
        BeginAsync(write: true, durable: synced, cancellationToken, "a rebuild repair", rebuild: true);

    /// <summary>This node's copy faults and rebuilds; null for stores built outside an engine.</summary>
    internal RebuildLedger? Ledger { get; init; }

    // A known fault refuses this exact open copy before recording it can wait on SYSTEM or the ledger gate. A new
    // incarnation has another store; a faulted store never clears this marker.
    internal string? CopyFault
    {
        get => Volatile.Read(ref field);
        set => Volatile.Write(ref field, value);
    }

    /// <summary>The current reason every native session must preserve this copy on close.</summary>
    internal string? CloseRefusal => CopyFault ?? Ledger?.Refusal(this);

    /// <summary>
    /// This database's rebuild record while the store is open, kept by <see cref="RebuildLedger"/>: what refuses its writes.
    /// The ledger publishes a fault here from any thread, so every access is volatile.
    /// </summary>
    internal RebuildLedger.Row? RebuildRow
    {
        get => Volatile.Read(ref field);
        set => Volatile.Write(ref field, value);
    }

    /// <summary>
    /// Whether <see cref="RebuildRow"/> was read under the ledger's gate after publication (<see cref="RebuildLedger.SettleAsync"/>):
    /// until then a record change may have missed this store, so it refuses ordinary writes.
    /// </summary>
    internal bool Settled
    {
        get => Volatile.Read(ref field);
        set => Volatile.Write(ref field, value);
    }

    /// <summary>The ledger's generation when <see cref="RebuildRow"/> was loaded, before publication.</summary>
    internal long LoadedGeneration { get; set; }

    /// <summary>Settles refusal before copying takes the engine's creation lock.</summary>
    internal async ValueTask<CopyOperation?> PrepareCopyAsync(CancellationToken cancellationToken)
    {
        if (Ledger is { } ledger && !IsSystem)
        {
            await ledger.SettleAsync(this, cancellationToken).ConfigureAwait(false);
        }

        ThrowIfRefused(rebuild: false);
        return await BeginOperationAsync(cancellationToken).ConfigureAwait(false);
    }

    // Throws when the ledger refuses a write now; a rebuild's own write is refused only when the node failed closed.
    private void ThrowIfRefused(bool rebuild)
    {
        if (Ledger is { } ledger && (rebuild ? ledger.FailedClosed : ledger.Refusal(this)) is { } refusal)
        {
            throw new ReplicaCopyFaultedException(refusal);
        }
    }

    /// <summary>Refuses a damaged copy, or the whole node for SYSTEM, before cleanup or durable recording can await.</summary>
    internal void NoteCopyFault(string reason)
    {
        CopyFault = reason;
        if (IsSystem)
        {
            Ledger?.FailClosed($"the system database faulted: {reason}");
        }
        else if (Ledger is { } ledger)
        {
            Flagged = ledger.FlagAsync(this);
        }
    }

    /// <summary>The latest <see cref="NoteCopyFault"/>'s durable <c>Faulting</c> mark; it never throws.</summary>
    internal Task Flagged
    {
        get => Volatile.Read(ref field);
        private set => Volatile.Write(ref field, value);
    } = Task.CompletedTask;

    /// <summary>
    /// Records that a transaction may have left this file uncertain or damaged: a copy fault of a client database, the
    /// node failing closed for the system database. Never throws; a fault that cannot be recorded fails the node closed.
    /// </summary>
    internal async Task RecordCopyFaultAsync(string reason)
    {
        if (Ledger is not { } ledger)
        {
            return;
        }

        if (IsSystem || Tenant is not { } tenant)
        {
            ledger.FailClosed($"the system database faulted: {reason}");
            return;
        }

        try
        {
            await ledger.RecordAsync(tenant, Incarnation, reason, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            ledger.FailClosed($"a copy fault could not be recorded: {error.GetType().Name}");
        }
    }

    // writer names a write transaction in the warning about a long wait for the writer. rebuild: a rebuild's own write,
    // which a copy under rebuild accepts.
    private async ValueTask<TursoStorageTransaction> BeginAsync(
        bool write, bool durable, CancellationToken cancellationToken, string writer = "a write transaction", bool rebuild = false,
        string? file = null, int line = 0)
    {
        if (write)
        {
            if (Ledger is { } ledger && !IsSystem)
            {
                await ledger.SettleAsync(this, cancellationToken).ConfigureAwait(false);
            }

            ThrowIfRefused(rebuild);
        }

        // Every write transaction syncs the write-ahead log at its commit (synchronous = FULL): what a write answers, the
        // system database's catalog and keys (decision 0012) and a client database's documents (0013), survives a power loss.
        // A rebuild transaction states its own: an ordinary write intent need only survive a process crash (0013d).
        durable |= write && !rebuild;
        Transactions?.Add(1, new KeyValuePair<string, object?>("kind", write ? "write" : "read"), new KeyValuePair<string, object?>("at", CallSite(file, line)));
        var ownsAdmission = !io.HasAdmission;
        if (ownsAdmission)
        {
            io.Admit();
        }
        var writing = false;
        CopyOperation? operation = null;
        TursoSession? session = null;
        var beginStarted = false;
        TursoRunner runner = write ? WriteLane : io;
        try
        {
            // Setup and BEGIN can also detect damage; record before opening a native session.
            operation = await io.WithAdmissionAsync(() => BeginOperationAsync(cancellationToken, intent: write)).ConfigureAwait(false);
            if (write)
            {
                await AcquireWriterAsync(writer, cancellationToken, file, line).ConfigureAwait(false);
                writing = true;
                // Again under the writer: a fault recorded while this one waited refuses it.
                ThrowIfRefused(rebuild);
            }

            session = write ? await WriterSessionAsync(cancellationToken).ConfigureAwait(false) : await RentAsync(runner, cancellationToken).ConfigureAwait(false);
            var connection = session.Connection;
            var transaction = await session.ExecuteAsync(runner, () =>
            {
                beginStarted = true;
                // BEGIN DEFERRED alone still permits a write upgrade. Turso enforces read-only
                // access before BEGIN; both setup and native lock acquisition share the caller's budget.
                // Turso refuses to change synchronous inside a transaction; with FULL the commit syncs the WAL (decision 0010).
                session.Settings(queryOnly: !write, full: durable);

                // Writers take the lock up front (BEGIN IMMEDIATE) so they wait instead of failing on upgrade.
                return connection.BeginTransaction(deferred: !write);
            }, cancellationToken).ConfigureAwait(false);
            return new TursoStorageTransaction(
                this, session, transaction, runner, write, write ? Committed : null, ownsAdmission)
            {
                Operation = operation,
            };
        }
        catch (Exception error)
        {
            var damaged = TursoStorageTransaction.Damaged(error);
            const string faultReason = "the file or its log reported an I/O error or corruption at BEGIN";
            if (damaged)
            {
                NoteCopyFault(faultReason);
            }

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
                operation?.Complete();
            }
            finally
            {
                try
                {
                    if (damaged)
                    {
                        // Damage opening the session or at BEGIN is a copy fault like a statement's, recorded before the
                        // writer is released.
                        await RecordCopyFaultAsync(faultReason).ConfigureAwait(false);
                    }
                }
                finally
                {
                    await EndAsync(writing, ownsAdmission, operation).ConfigureAwait(false);
                }
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
    private async ValueTask AcquireWriterAsync(string writer, CancellationToken cancellationToken, string? file = null, int line = 0)
    {
        var started = Time.GetTimestamp();
        var outcome = "and took it";
        try
        {
            if (_writer.CurrentCount == 0 && Tenant is { } tenant)
            {
                Checkpoint?.Invoke("writer.waiting", tenant);
            }

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
                var holderAt = CallSite(Volatile.Read(ref _writerFile), Volatile.Read(ref _writerLine));
                var writerAt = CallSite(file, line);
                var held = Time.GetElapsedTime(Volatile.Read(ref _writerTaken));
                // Counts, kinds and code locations only: no database or tenant names in the log.
                logger.ZLogWarning($"Waited {Math.Round(waited.TotalSeconds, 1):@Waited} s for a database's writer {outcome:@Outcome}: {writer:@Writer} ({writerAt:@WriterAt}) queued behind {holder:@Holder} ({holderAt:@HolderAt}), which had held it for {Math.Round(held.TotalSeconds, 1):@Held} s and was {(WriteLane.Running ? "running a storage call" : "between storage calls"):@State}");
            }
        }

        // Only this holder writes these until it releases the writer.
        var taken = Time.GetTimestamp();
        Volatile.Write(ref _writerTaken, taken);
        Volatile.Write(ref _writerHolder, writer);
        Volatile.Write(ref _writerFile, file);
        Volatile.Write(ref _writerLine, line);
        _holdStarted = taken;
    }

    // A caller's source file name and line, as the compiler recorded them; "an engine caller" when none was passed.
    private static string CallSite(string? file, int line) =>
        string.IsNullOrEmpty(file) ? "an engine caller" : $"{Path.GetFileName(file.Replace('\\', '/'))}:{line}";

    // Releases _writer. Whoever releases it after the store closed closes the writer connection, so it is closed exactly
    // once, never under a running writer.
    private void ReleaseWriter()
    {
        try
        {
            if (_writerSession is { } stale && !Closed && stale.Statistics != Volatile.Read(ref _statistics))
            {
                _writerSession = null;
                Revisions.Detach();
                pool.Discard(stale);
            }

            if (Closed && _writerSession is { } session)
            {
                _writerSession = null;
                Revisions.Detach();
                pool.Discard(session);
            }
        }
        finally
        {
            var held = TimeSpan.Zero;
            var holder = _writerHolder;
            var holderAt = CallSite(_writerFile, _writerLine);
            if (_holdStarted is var started and not 0)
            {
                _holdStarted = 0;
                held = Time.GetElapsedTime(started);
                // The call site too (file:line, a bounded set): a kind alone does not say which code holds the writers.
                WriterHeld?.Record(held.TotalSeconds, new KeyValuePair<string, object?>("holder", holder), new KeyValuePair<string, object?>("at", holderAt));
            }

            _writer.Release();
            if (held >= LongWriterHold)
            {
                LogLongHold(held, holder, holderAt);
            }
        }
    }

    // Batch 9 (2026-10-26): a durable write held gru's writer for 1 to 2.5 s with nothing waiting, so no slow-writer line
    // named it. Logged after the release: the log cannot delay or keep the writer.
    private void LogLongHold(TimeSpan held, string? holder, string holderAt)
    {
        try
        {
            // Kinds and code locations only: no database or tenant names in the log.
            logger?.ZLogWarning($"A database's writer was held {Math.Round(held.TotalSeconds, 1):@Held} s by {holder ?? "nothing recorded":@Holder} ({holderAt:@HolderAt})");
        }
        catch (Exception)
        {
            // A failing logger must not fail the release of a writer that is already free.
        }
    }

    // The only connection that writes this file: Turso aborts the process when two connections write one file
    // (docs/terrabase/turso-spike.md). The first writer opens it on the write lane, with change capture on, and the store
    // keeps it until it closes. Only the holder of _writer uses it.
    private TursoSession? _writerSession;

    /// <summary>The writer connection while one is open: which session the next write transaction uses.</summary>
    internal TursoSession? CurrentWriter() => _writerSession;

    /// <summary>The host's fault injection point (a no-op in production), for a tenant database's lifecycle steps.</summary>
    internal DatabaseLifecycleCheckpoint? Checkpoint { get; init; }

    /// <summary>
    /// The writer connection; the caller holds the writer. Every change it commits is captured in <c>turso_cdc</c>, mode
    /// <c>after</c> (docs/terrabase/multi-region-replication.md section 7), unless a transaction turns capture off with
    /// <see cref="TursoStorageTransaction.SuspendCaptureAsync"/>.
    /// </summary>
    private async Task<TursoSession> WriterSessionAsync(CancellationToken cancellationToken)
    {
        if (_writerSession is { } open)
        {
            return open;
        }

        await WhileRewritingAsync(() => pool.OpenNew(this), cancellationToken).ConfigureAwait(false);
        TursoSession session;
        try
        {
            var statistics = Volatile.Read(ref _statistics);
            var connectionString = await ConnectionStringAsync(WriteLane, cancellationToken).ConfigureAwait(false);
            session = await TursoSession.OpenAsync(WriteLane, connectionString, pragmas, cancellationToken, this).ConfigureAwait(false);
            session.Statistics = statistics;
        }
        catch (Exception error)
        {
            pool.OpenFailed(this);
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
            if (Captures)
            {
                await session.ExecuteAsync(WriteLane, () => Scalar(session.Connection, null, TursoSession.CaptureOn), cancellationToken).ConfigureAwait(false);
            }

            if (CloseRefusal is null)
            {
                await session.ExecuteAsync(WriteLane, () =>
                {
                    Revisions.Attach(session.Connection);
                    return true;
                }, cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            await WriteLane.RunAsync(() => pool.Discard(session), CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        return _writerSession = session;
    }

    /// <summary>Releases what <see cref="BeginTransactionAsync"/> took; called once per transaction.</summary>
    internal async ValueTask EndAsync(bool write, bool ownsAdmission, CopyOperation? operation)
    {
        try
        {
            if (write)
            {
                try
                {
                    await IndexRevisionAsync().ConfigureAwait(false);
                }
                finally
                {
                    // Closing a stale writer connection can throw; the admission is released all the same.
                    ReleaseWriter();
                }
            }
        }
        finally
        {
            try
            {
                if (operation is not null)
                {
                    await operation.DisposeAsync().ConfigureAwait(false);
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
    }

    /// <summary>
    /// Writes a consistent snapshot to <paramref name="path"/>; the caller awaited <see cref="PrepareCopyAsync"/> before
    /// taking creation and holds an admission.
    /// </summary>
    internal async Task<Ulid> CopyToAsync(string path, CancellationToken cancellationToken)
    {
        const string reason = "the source file or its log reported an I/O error or corruption while copying";
        ExceptionDispatchInfo? sourceError = null;
        try
        {
            // Row by row in one read transaction (TursoCopy): writes go on meanwhile. Only source operations mark damage;
            // the interleaved target writes and its initialization must not fault a healthy source.
            var sourceConnection = await ConnectionStringAsync(io, cancellationToken).ConfigureAwait(false);
            var targetConnection = await Encryption.CreateAsync(path, io, cancellationToken).ConfigureAwait(false);
            await io.RunAsync(() =>
            {
                using var source = new FaultAwareCopySource(sourceConnection, error =>
                {
                    NoteCopyFault(reason);
                    sourceError ??= ExceptionDispatchInfo.Capture(error);
                })
                {
                    CloseRefusal = () => CloseRefusal ?? "terminal copy source close",
                };
                if (Tenant is { } tenant)
                {
                    Checkpoint?.Invoke("copy.source.opening", tenant);
                }
                TursoSession.OpenExisting(source, FilePath);
                using var begin = source.CreateCommand();
                begin.CommandText = "BEGIN;";
                begin.ExecuteNonQuery();
                TursoCopy.Write(source, targetConnection, cancellationToken);
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception) when (sourceError is not null)
        {
            // Await off the I/O worker, retaining creation and its admission until the FULL record commits (or fails closed).
            _ = await io.WithAdmissionAsync(async () =>
            {
                await RecordCopyFaultAsync(reason).ConfigureAwait(false);
                return true;
            }).ConfigureAwait(false);
            // Reader/connection cleanup can fail too; preserve the first native source error that withdrew trust.
            sourceError.Throw();
            throw;
        }

        // InitializeAsync switches the copy to WAL and refuses an unsupported schema version.
        if (Tenant is { } targetSource)
        {
            Checkpoint?.Invoke("copy.target.initializing", targetSource);
        }
        return await InitializeAsync(name, path, io, pragmas, Encryption, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The database file.</summary>
    internal string FilePath => path;

    // When the file was last seen in place (Environment.TickCount64), and the engine's file epoch then.
    private long _fileSeenAt;
    private long _fileSeenEpoch = -1;

    /// <summary>
    /// Whether the database file is still in place. An open store keeps its handles, so the path is looked up (an lstat) at
    /// most once a second, and again whenever TerraBase's own file operations ran since (<paramref name="epoch"/> moved): only
    /// a deletion outside TerraBase can go unseen, for at most a second.
    /// </summary>
    internal bool FilePresent(long epoch)
    {
        var now = Environment.TickCount64;
        if (Volatile.Read(ref _fileSeenEpoch) == epoch && now - Volatile.Read(ref _fileSeenAt) < 1000)
        {
            return true;
        }

        if (!File.Exists(path))
        {
            return false;
        }

        Volatile.Write(ref _fileSeenAt, now);
        Volatile.Write(ref _fileSeenEpoch, epoch);
        return true;
    }

    private long _backupAnchor = -1;

    /// <summary>
    /// The earliest change id held by continuous backup or a rewrite, or null when neither holds this file: CDC retention
    /// keeps the rows from it on (<see cref="TursoStorageTransaction.RecordAckedAsync"/>).
    /// </summary>
    internal long? BackupAnchor
    {
        get
        {
            var backup = Volatile.Read(ref _backupAnchor);
            var rewrite = RewriteAnchor;
            var anchor = backup < 0 ? rewrite : rewrite < 0 ? backup : Math.Min(backup, rewrite);
            return anchor >= 0 ? anchor : null;
        }
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
            try
            {
                await IndexRevisionAsync().ConfigureAwait(false);
            }
            finally
            {
                ReleaseWriter();
            }
        }
    }

    /// <summary>
    /// Runs <paramref name="call"/> on the writer connection, on the write lane, while no write transaction is open; for
    /// work that writes outside a transaction or must see every commit (a snapshot, a copy, change-capture cleanup).
    /// </summary>
    internal Task<T> WithWriterAsync<T>(Func<TursoSession, T> call, string holder, CancellationToken cancellationToken) =>
        WithWritersHeldAsync(() => OnWriterAsync(call, cancellationToken), cancellationToken, holder);

    /// <summary>Runs <paramref name="call"/> on the writer connection; the caller holds the writer (<see cref="WithWritersHeldAsync"/>).</summary>
    internal async Task<T> OnWriterAsync<T>(Func<TursoSession, T> call, CancellationToken cancellationToken)
    {
        await using var operation = await BeginOperationAsync(cancellationToken).ConfigureAwait(false);
        TursoSession? writer = null;
        try
        {
            writer = await WriterSessionAsync(cancellationToken).ConfigureAwait(false);
            var result = await writer.ExecuteAsync(WriteLane, () => call(writer), cancellationToken).ConfigureAwait(false);
            operation?.Complete();
            return result;
        }
        catch (Exception error)
        {
            if (TursoStorageTransaction.Damaged(error))
            {
                NoteCopyFault("writer maintenance reported I/O failure or corruption");
                await RecordCopyFaultAsync(CopyFault!).ConfigureAwait(false);
            }
            // The connection may be left mid-statement: the next writer opens a fresh one.
            if (writer is not null)
            {
                await WriteLane.RunAsync(() => Discard(writer), CancellationToken.None).ConfigureAwait(false);
            }
            operation?.Complete();
            throw;
        }
    }

    // Group commit (docs/design/anna-core.md section 4.1): client writes queue here, and one loop at a time runs a batch
    // of them in one write transaction, each inside its own savepoint, so the database's single Turso writer commits
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

    /// <summary>Runs inside a transaction's CommitAsync right after its Turso commit succeeded; integration tests fail it.</summary>
    internal Action? AfterTursoCommit { get; set; }

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

    // Group commit: the batch is the holder ("a group commit batch"); its writes' callers are many, so none is named.
    public async ValueTask<(T Result, CommitResult Commit)> WriteAsync<T>(
        Func<IStorageTransaction, CancellationToken, ValueTask<T>> work, Func<T, bool> keep, CancellationToken cancellationToken,
        [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
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
        // Each batch holds the writer for all its calls: they run on the write lane's thread back to back.
        WriteLane.Resident();
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
        var commit = default(CommitResult);
        TursoStorageTransaction? transaction = null;
        try
        {
            transaction = await BeginAsync(write: true, durable: false, CancellationToken.None, "a group commit batch").ConfigureAwait(false);
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
                        write.Changed = transaction.SavepointHasChanges;
                        transaction.ReleaseSavepoint();
                        kept.Add(write);
                        continue;
                    }

                    await transaction.RollbackToSavepointAsync().ConfigureAwait(false);
                    (transaction.LimitsReached && kept.Count > 0 ? deferred : refused).Add(write);
                }

                if (kept.Count > 0)
                {
                    commit = await transaction.CommitAsync(CancellationToken.None).ConfigureAwait(false);
                    AfterGroupCommit?.Invoke();
                }
            }
        }
        catch (Exception error) when (error is not ReplicaCopyFaultedException && CopyFault is null && Ledger?.FailedClosed is null
            && !IsSystem && transaction?.Committed is { } committedAt && !transaction.ChangedReaderState)
        {
            // A client database's batch committed and changed no state readers see beside its documents: its writes are
            // answered as committed, not as a failure a client would retry. What failed is a wake-up or the cleanup after
            // the commit (disposing the transaction, releasing the writer).
            commit = committedAt;
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
            // No usable commit result, including a failed marker read after COMMIT: never replay an ambiguous batch.
            batch.ForEach(write => write.Fail(error));
            return [];
        }

        // Disposal can publish damage without throwing after a landed commit: fail without replaying its writes.
        if ((Ledger?.FailedClosed ?? CopyFault) is { } fault)
        {
            batch.ForEach(write => write.Fail(new ReplicaCopyFaultedException(fault)));
            return [];
        }

        // After the transaction released its session, as an admission's resource must be (TursoIoPool).
        refused.ForEach(write => write.Answer(default));
        kept.ForEach(write => write.Answer(write.Changed ? commit : commit with { Marker = null }));
        return deferred;
    }

    private interface IBatchedWrite
    {
        bool Cancelled { get; }

        bool Changed { get; set; }

        ValueTask<bool> RunAsync(IStorageTransaction transaction);

        void Answer(CommitResult commit);

        void Fail(Exception error);

        void Cancel();
    }

    private sealed class BatchedWrite<T>(
        Func<IStorageTransaction, CancellationToken, ValueTask<T>> work, Func<T, bool> keep, CancellationToken cancellationToken) : IBatchedWrite
    {
        private readonly TaskCompletionSource<(T, CommitResult)> _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private T _result = default!;
        private readonly QosContext.Snapshot _context = QosContext.Capture();

        public Task<(T Result, CommitResult Commit)> Task => _done.Task;

        public bool Cancelled => cancellationToken.IsCancellationRequested;

        public bool Changed { get; set; }

        public async ValueTask<bool> RunAsync(IStorageTransaction transaction)
        {
            // Borrow request ownership while retaining the original batch's admission across awaits.
            using var scope = QosContext.Restore(_context);
            _result = await work(transaction, cancellationToken).ConfigureAwait(false);
            return keep(_result);
        }

        public void Answer(CommitResult commit) => _done.TrySetResult((_result, commit));

        public void Fail(Exception error) => _done.TrySetException(error);

        public void Cancel() => _done.TrySetCanceled(cancellationToken);
    }

    // The collections of this database, loaded on first use and forgotten by every commit that changes them, so point
    // operations and shape polls do not open a Turso read transaction to find their collection.
    private volatile FrozenDictionary<string, CollectionInfo>? _collections;
    private long _collectionsVersion;

    public async ValueTask<CollectionInfo?> FindCollectionAsync(string name, CancellationToken cancellationToken)
    {
        if (_collections is { } known)
        {
            return known.GetValueOrDefault(name);
        }

        var version = Interlocked.Read(ref _collectionsVersion);
        var names = new Dictionary<string, CollectionInfo>(StringComparer.Ordinal);
        var transaction = await BeginAsync(write: false, durable: false, cancellationToken).ConfigureAwait(false);
        await using (transaction.ConfigureAwait(false))
        {
            string? after = null;
            while (true)
            {
                var (page, hasMore) = await transaction.ListCollectionsAsync(after, 1000, cancellationToken).ConfigureAwait(false);
                foreach (var collection in page)
                {
                    names.Add(collection.Name, collection);
                }

                if (!hasMore || page.Count == 0)
                {
                    break;
                }

                after = page[^1].Name;
            }
        }

        var loaded = names.ToFrozenDictionary(StringComparer.Ordinal);

        // A commit that changed the collections while this load ran leaves the set unknown instead of stale.
        lock (_queueGate)
        {
            if (Interlocked.Read(ref _collectionsVersion) == version)
            {
                _collections = loaded;
            }
        }

        return loaded.GetValueOrDefault(name);
    }

    /// <summary>
    /// The committed row of a collection from the names readers already loaded, without a query: false while they are not
    /// loaded. Only for a write transaction that changed no collection: every commit that does forgets the names.
    /// </summary>
    internal bool TryKnownCollection(string name, out CollectionInfo? collection)
    {
        if (_collections is { } known)
        {
            collection = known.GetValueOrDefault(name);
            return true;
        }

        collection = null;
        return false;
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

    /// <summary>Takes back a session whose work has ended; the pool keeps it idle or closes it.</summary>
    /// <remarks>Call on an I/O thread; idle eviction only deinitializes quiescent handles.</remarks>
    internal void Return(TursoSession session)
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
    internal void Discard(TursoSession session)
    {
        if (session == _writerSession)
        {
            _writerSession = null;
            Revisions.Detach();
        }

        pool.Discard(session);
    }

    private Task ReturnAsync(TursoSession session) => io.RunAsync(() => Return(session), CancellationToken.None);

    /// <summary>
    /// Closes idle sessions and refuses new ones; a rented session is closed when it is returned.
    /// The engine calls this when the database is deleted and at shutdown.
    /// </summary>
    public void Dispose()
    {
        Revisions.Dispose();
        pool.Close(this);
        // A writer holding _writer now closes the connection itself when it releases it.
        if (_writer.Wait(0))
        {
            ReleaseWriter();
        }
    }

    // The caller holds an admission.
    private Task<TursoSession> RentAsync(CancellationToken cancellationToken) => RentAsync(io, cancellationToken);

    // Opens a session on runner when none is idle: write transactions open theirs on the write lane.
    private async Task<TursoSession> RentAsync(TursoRunner runner, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // A deleted or shut-down database hands out no session.
        if (await WhileRewritingAsync(() => pool.Rent(this), cancellationToken).ConfigureAwait(false) is { } idle)
        {
            return idle;
        }

        TursoSession session;
        try
        {
            var statistics = Volatile.Read(ref _statistics);
            var connectionString = await ConnectionStringAsync(runner, cancellationToken).ConfigureAwait(false);
            session = await TursoSession.OpenAsync(runner, connectionString, pragmas, cancellationToken, this).ConfigureAwait(false);
            session.Statistics = statistics;
        }
        catch (Exception error)
        {
            // Rent counted an opening even when cancellation removed the queued Open before it started.
            pool.OpenFailed(this);
            if (Closed && !cancellationToken.IsCancellationRequested)
            {
                // A delete can unlink the file after Rent found no session but before Turso opened it.
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
    /// while it is built: one fsync of the finished file replaces the eight or so of Turso's journal switch, commits and
    /// closing checkpoint. Creation holds the engine's creation lock, so on a slow disk those syncs queued every other
    /// creation behind them (TS-647).
    /// </remarks>
    internal static async Task<Ulid> InitializeAsync(
        string name, string path, TursoIoPool io, string pragmas, DatabaseKeys encryption, CancellationToken cancellationToken)
    {
        var connectionString = File.Exists(path)
            ? await encryption.ExistingAsync(path, io, cancellationToken).ConfigureAwait(false)
            : await encryption.CreateAsync(path, io, cancellationToken).ConfigureAwait(false);
        return await InitializeAsync(name, path, io, pragmas, connectionString, cancellationToken).ConfigureAwait(false);
    }

    // A healthy rewrite retains the original key so the single-file rename is also atomic with respect to its key.
    internal static Task<Ulid> InitializeAsync(
        string name, string path, TursoIoPool io, string pragmas, string connectionString, CancellationToken cancellationToken) =>
        io.RunAsync(() =>
        {
            Ulid incarnation;
            using (var connection = new SqliteConnection(connectionString))
            {
                connection.Open();
                // The creating connection uses the same settings as every pooled one, except that it does not sync: the
                // file is private until it is published, and synced once then. Turso's journal is WAL by default.
                Scalar(connection, null, pragmas + "PRAGMA synchronous = OFF;");
                Bootstrap(name, connection, cancellationToken);
                // Publish a fresh identity, on this file only.
                using var transaction = connection.BeginTransaction(deferred: false);
                incarnation = RenewIncarnation(connection, transaction);
                cancellationToken.ThrowIfCancellationRequested();
                transaction.Commit();
                // Turso does not checkpoint when the last connection closes: copy the log into the file, which is moved
                // without its sidecars.
                Scalar(connection, null, "PRAGMA wal_checkpoint(TRUNCATE);");
            }

            // The file alone now holds the database: sync it once, before it can be published.
            using var file = File.OpenHandle(path, FileMode.Open, FileAccess.ReadWrite);
            RandomAccess.FlushToDisk(file);
            return incarnation;
        }, cancellationToken);

    // The caller holds an admission.
    internal async Task BootstrapAsync(CancellationToken cancellationToken)
    {
        var session = await RentAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await session.ExecuteAsync(io, () =>
            {
                // A pooled session may still be read-only from its last transaction.
                session.Settings(queryOnly: false, full: false);
                // Bootstrap is atomic and rolls back before a lock error leaves this scope.
                Bootstrap(name, session.Connection, cancellationToken);
                _incarnation = new(Ulid.Parse(
                    (string)(Scalar(session.Connection, null, TursoStatements.Incarnation)
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
        var schema = name == TursoStorageEngine.SystemName ? SystemSchemaSql : ClientSchemaSql;
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
    private static Ulid RenewIncarnation(SqliteConnection connection, SqliteTransaction? transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = TursoStatements.RenewIncarnation;
        var incarnation = EntityIds.New();
        command.Parameters.AddWithValue("$incarnation", incarnation.ToString());
        command.ExecuteNonQuery();
        return incarnation;
    }

    private static object? Scalar(SqliteConnection connection, SqliteTransaction? transaction, string sql)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return command.ExecuteScalar();
    }
}

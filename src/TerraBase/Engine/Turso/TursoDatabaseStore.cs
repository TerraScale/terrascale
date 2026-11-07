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

internal sealed partial class TursoDatabaseStore(
    string name, string path, TursoSessionPool pool, TursoIoPool io, string pragmas, TenantContext? tenant, TimeProvider? time,
    Action? committed, ILogger? logger = null, IMemoryBudget? schemaMemory = null,
    Counter<long>? storageReads = null, DatabaseKeys? encryption = null) : IDatabaseStore, IDisposable
{
    internal DatabaseKeys Encryption { get; } = encryption ?? throw new ArgumentNullException(nameof(encryption));

    internal IMemoryBudget? SchemaMemory { get; } = schemaMemory;
    internal Counter<long>? StorageReads { get; } = storageReads;

    private long _retainedCdcRows;

    internal long RetainedCdcRows => Volatile.Read(ref _retainedCdcRows);

    internal void ObserveCdcSize(SqliteConnection connection, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT coalesce((SELECT max(change_id) FROM turso_cdc) - (SELECT min(change_id) FROM turso_cdc) + 1, 0);";
        Volatile.Write(ref _retainedCdcRows, (long)command.ExecuteScalar()!);
    }

    internal TursoDatabaseStore(string name, string path, TursoSessionPool pool, TursoIoPool io, string pragmas, DatabaseKeys encryption, TimeProvider? time = null, Action? committed = null)
        : this(name, path, pool, io, pragmas, null, time, committed, encryption: encryption) { }

    private const int SchemaVersion = 1;

    internal static readonly byte[] FormatDigest = SHA256.HashData(Encoding.UTF8.GetBytes(SystemSchemaSql + ClientSchemaSql));

    private const string OffsetSchemaSql = """
        CREATE TABLE commit_offset (
            id INTEGER PRIMARY KEY CHECK (id = 1), value INTEGER NOT NULL, incarnation TEXT NOT NULL
        );
        INSERT INTO commit_offset (id, value, incarnation) VALUES (1, 0, '');
        """;

    // TerraBase's own records are plain rows: one per kind and id, stamped with the commit offset of the transaction that
    // wrote it. Only the system database keeps them.
    private const string SystemSchemaSql = OffsetSchemaSql + """
        CREATE TABLE _tb_records (
            kind TEXT NOT NULL, id TEXT NOT NULL, version INTEGER NOT NULL, body TEXT NOT NULL, UNIQUE (kind, id)
        );
        CREATE TABLE _tb_record_kinds (
            name TEXT PRIMARY KEY, created_at INTEGER NOT NULL,
            entity_id TEXT NOT NULL CHECK (length(entity_id) = 26)
        );
        CREATE TABLE _tb_fault (
            organization TEXT NOT NULL, project TEXT NOT NULL, database TEXT NOT NULL,
            incarnation TEXT NOT NULL, reason TEXT NOT NULL CHECK (length(reason) <= 256),
            UNIQUE (organization, project, database)
        );
        CREATE TABLE _tb_write_intent (
            organization TEXT NOT NULL, project TEXT NOT NULL, database TEXT NOT NULL, incarnation TEXT NOT NULL, run INTEGER NOT NULL,
            UNIQUE (organization, project, database)
        );
        """;

    private const string ClientSchemaSql = OffsetSchemaSql + """
        CREATE TABLE _tb_sql_storage (id INTEGER PRIMARY KEY CHECK (id = 1), bytes INTEGER NOT NULL);
        INSERT INTO _tb_sql_storage (id, bytes) VALUES (1, 0);
        CREATE TABLE _tb_shape_chunk (
            handle TEXT NOT NULL, offset INTEGER NOT NULL, end_offset INTEGER NOT NULL,
            body BLOB NOT NULL, sealed_at INTEGER NOT NULL, UNIQUE (handle, offset)
        );
        CREATE TABLE _tb_shape_snapshot (
            handle TEXT NOT NULL, offset INTEGER NOT NULL, page INTEGER NOT NULL, next_id TEXT,
            body BLOB NOT NULL, sealed_at INTEGER NOT NULL, UNIQUE (handle, offset, page)
        );
        """;

    internal Task<string> ConnectionStringAsync(TursoRunner runner, CancellationToken cancellationToken) =>
        Encryption.ExistingAsync(FilePath, runner, cancellationToken);

    private readonly Lock _commits = new();
    private long _sequence;
    private TaskCompletionSource _signal = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal bool IsSystem => name == TursoStorageEngine.SystemName;

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

    private readonly SemaphoreSlim _writer = new(1, 1);

    internal static readonly TimeSpan SlowWriterWait = TimeSpan.FromSeconds(5);

    internal static readonly TimeSpan LongWriterHold = TimeSpan.FromSeconds(1);

    private string? _writerHolder;
    private long _writerTaken;

    private string? _writerFile;
    private int _writerLine;

    private long _holdStarted;

    internal Histogram<double>? WriterHeld { get; init; }

    internal Counter<long>? Transactions { get; init; }

    internal TursoWriteLane WriteLane { get; } = new(name, io);

    internal TursoRevisionLedger Revisions { get; } = new(path, schemaMemory, tenant);
    private volatile StrongBox<Ulid> _incarnation = new(Ulid.Empty);

    public Ulid Incarnation => _incarnation.Value;

    public TenantContext? Tenant { get; } = tenant;

    internal bool Closed
    {
        get => Volatile.Read(ref field);
        set => Volatile.Write(ref field, value);
    }

    public bool IsClosed => Closed;

    internal TimeProvider Time => time ?? TimeProvider.System;

    internal TimeProvider HostTime { get; init; } = TimeProvider.System;

    internal static readonly TimeSpan DefaultQueryBudget = TimeSpan.FromSeconds(TerraBaseOptions.DefaultQueryBudgetSeconds);

    internal TimeSpan QueryBudget { get; init; } = DefaultQueryBudget;

    internal int IdleCount => pool.IdleCountOf(this);

    public async ValueTask<IStorageTransaction> BeginTransactionAsync(
        bool write, CancellationToken cancellationToken, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0) =>
        await BeginAsync(write, durable: false, cancellationToken, file: file, line: line).ConfigureAwait(false);

    public async ValueTask<IStorageTransaction> BeginDurableTransactionAsync(
        CancellationToken cancellationToken, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0) =>
        await BeginAsync(write: true, durable: true, cancellationToken, "a durable write transaction", file: file, line: line).ConfigureAwait(false);

    internal ValueTask<TursoStorageTransaction> BeginMaintenanceTransactionAsync(CancellationToken cancellationToken, bool synced = true) =>
    IsSystem ? BeginAsync(write: true, durable: synced, cancellationToken, "storage fault bookkeeping", maintenance: true)
        : throw new InvalidOperationException("fault bookkeeping requires a system store");

    internal StorageFaultLedger? Ledger { get; init; }

    internal DatabaseFences? Fences { get; init; }

    internal string? CopyFault
    {
        get => Volatile.Read(ref field);
        set => Volatile.Write(ref field, value);
    }

    internal string? CloseRefusal => CopyFault ?? Ledger?.Refusal(this);

    internal StorageFaultLedger.Row? FaultRow
    {
        get => Volatile.Read(ref field);
        set => Volatile.Write(ref field, value);
    }

    internal bool Settled
    {
        get => Volatile.Read(ref field);
        set => Volatile.Write(ref field, value);
    }

    internal long LoadedGeneration { get; set; }

    internal async ValueTask<CopyOperation?> PrepareCopyAsync(CancellationToken cancellationToken)
    {
        if (Ledger is { } ledger && !IsSystem)
        {
            await ledger.SettleAsync(this, cancellationToken).ConfigureAwait(false);
        }

        ThrowIfRefused(maintenance: false);
        return await BeginOperationAsync(cancellationToken).ConfigureAwait(false);
    }

    private string? FencedId(bool maintenance) => !maintenance && !IsSystem && Fences is not null ? Tenant?.DatabaseId : null;

    internal Task DrainWritersAsync(CancellationToken cancellationToken) => WithWriterAsync(static _ => 0, "a home move", cancellationToken);
    internal HomeLeaseGate? Leases { get; init; }

    private string? LeasedId => !IsSystem && Leases is not null ? Tenant?.DatabaseId : null;

    internal void ThrowIfUnleased(string database)
    {
        Fences?.ThrowIfFenced(database);
        Leases?.ThrowIfUnleased(database);
    }

    private void ThrowIfUnleased(bool maintenance)
    {
        if (!maintenance && LeasedId is { } database)
        {
            Leases!.ThrowIfUnleased(database);
        }
    }

    private void ThrowIfRefused(bool maintenance)
    {
        if (Ledger is { } ledger && (maintenance ? ledger.FailedClosed : ledger.Refusal(this)) is { } refusal)
        {
            throw new StorageCopyFaultedException(refusal);
        }
    }

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

    internal Task Flagged
    {
        get => Volatile.Read(ref field);
        private set => Volatile.Write(ref field, value);
    } = Task.CompletedTask;

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
            await ledger.RecordAsync(tenant, Incarnation, reason).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            ledger.FailClosed($"a copy fault could not be recorded: {error.GetType().Name}");
        }
    }

    private async ValueTask<TursoStorageTransaction> BeginAsync(
        bool write, bool durable, CancellationToken cancellationToken, string writer = "a write transaction", bool maintenance = false,
        string? file = null, int line = 0)
    {
        if (write)
        {
            if (Ledger is { } ledger && !IsSystem)
            {
                await ledger.SettleAsync(this, cancellationToken).ConfigureAwait(false);
            }

            ThrowIfRefused(maintenance);
            if (FencedId(maintenance) is { } fenced)
            {
                await Fences!.WaitAsync(fenced, cancellationToken).ConfigureAwait(false);
            }
            ThrowIfUnleased(maintenance);
        }

        durable |= write && !maintenance;
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

            operation = await io.WithAdmissionAsync(() => BeginOperationAsync(cancellationToken, intent: write)).ConfigureAwait(false);
            if (write)
            {
                await AcquireWriterAsync(writer, cancellationToken, file, line).ConfigureAwait(false);
                writing = true;

                ThrowIfRefused(maintenance);
                if (FencedId(maintenance) is { } fenced)
                {
                    Fences!.ThrowIfFenced(fenced);
                }
                ThrowIfUnleased(maintenance);
            }

            session = write ? await WriterSessionAsync(cancellationToken).ConfigureAwait(false) : await RentAsync(runner, cancellationToken).ConfigureAwait(false);
            var connection = session.Connection;
            var transaction = await session.ExecuteAsync(runner, () =>
            {
                beginStarted = true;

                session.Settings(queryOnly: !write, full: durable);

                return connection.BeginTransaction(deferred: !write);
            }, cancellationToken).ConfigureAwait(false);
            return new TursoStorageTransaction(
                this, session, transaction, runner, write, write ? Committed : null, ownsAdmission)
            {
                Operation = operation,
                Leased = write && !maintenance ? LeasedId : null,
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

                    if (beginStarted)
                    {
                        await runner.RunAsync(() => Discard(session), CancellationToken.None).ConfigureAwait(false);
                    }
                }
                else if (session is not null && (beginStarted || !pool.TryReturn(this, session)))
                {

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

                        await RecordCopyFaultAsync(faultReason).ConfigureAwait(false);
                    }
                }
                finally
                {
                    await EndAsync(writing, ownsAdmission, operation, indexRevision: beginStarted).ConfigureAwait(false);
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

            MoveCommitCount();
        }
    }

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

    internal void LogAfterCommit(string step, Exception error)
    {
        try
        {
            logger?.ZLogError(error, $"Database {name:@Database} committed, but {step:@Step} after its commit failed");
        }
        catch (Exception)
        {

        }
    }

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

                logger.ZLogWarning($"Waited {Math.Round(waited.TotalSeconds, 1):@Waited} s for a database's writer {outcome:@Outcome}: {writer:@Writer} ({writerAt:@WriterAt}) queued behind {holder:@Holder} ({holderAt:@HolderAt}), which had held it for {Math.Round(held.TotalSeconds, 1):@Held} s and was {(WriteLane.Running ? "running a storage call" : "between storage calls"):@State}");
            }
        }

        TookWriter(writer, file, line);
    }

    private void TookWriter(string writer, string? file, int line)
    {
        var taken = Time.GetTimestamp();
        Volatile.Write(ref _writerTaken, taken);
        Volatile.Write(ref _writerHolder, writer);
        Volatile.Write(ref _writerFile, file);
        Volatile.Write(ref _writerLine, line);
        _holdStarted = taken;
    }

    private static string CallSite(string? file, int line) =>
        string.IsNullOrEmpty(file) ? "an engine caller" : $"{Path.GetFileName(file.Replace('\\', '/'))}:{line}";

    private void ReleaseWriter()
    {
        try
        {
            if (_writerSession is { } stale && !Closed && stale.Statistics != Volatile.Read(ref _statistics))
            {
                _writerSession = null;
                Revisions.Detach();
                pool.ForgetWriter(this);
                pool.Discard(stale);
            }

            if (Closed && _writerSession is { } session)
            {
                _writerSession = null;
                Revisions.Detach();
                pool.ForgetWriter(this);
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

                WriterHeld?.Record(held.TotalSeconds, new KeyValuePair<string, object?>("holder", holder), new KeyValuePair<string, object?>("at", holderAt));
            }

            _writer.Release();
            if (held >= LongWriterHold)
            {
                LogLongHold(held, holder, holderAt);
            }
        }
    }

    private void LogLongHold(TimeSpan held, string? holder, string holderAt)
    {
        try
        {

            logger?.ZLogWarning($"A database's writer was held {Math.Round(held.TotalSeconds, 1):@Held} s by {holder ?? "nothing recorded":@Holder} ({holderAt:@HolderAt})");
        }
        catch (Exception)
        {

        }
    }

    private TursoSession? _writerSession;

    internal TursoSession? CurrentWriter() => _writerSession;

    internal DatabaseLifecycleCheckpoint? Checkpoint { get; init; }

    private bool Evictable => Tenant is not null && !IsSystem && Captures;

    private async Task<TursoSession> WriterSessionAsync(CancellationToken cancellationToken)
    {
        if (_writerSession is { } open)
        {
            UseWriter();
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

        _writerSession = session;
        UseWriter();
        return session;
    }

    private void UseWriter()
    {
        if (!Evictable || pool.UseWriter(this) is not { } evicted)
        {
            return;
        }

        using (ExecutionContext.SuppressFlow())
        {
            foreach (var store in evicted)
            {
                _ = Task.Run(store.EvictWriterAsync);
            }
        }
    }

    internal async Task EvictWriterAsync()
    {
        if (Closed || _writerSession is null || !_writer.Wait(0))
        {
            return;
        }

        TookWriter("a writer eviction", null, 0);
        var admitted = false;
        try
        {
            io.Admit();
            admitted = true;
            if (Closed || CloseRefusal is not null || _writerSession is not { } writer || pool.Paused(this) || pool.BusyOf(this, writer) > 0)
            {
                return;
            }

            await using var operation = await BeginOperationAsync(CancellationToken.None).ConfigureAwait(false);
            await WriteLane.RunAsync(() =>
            {
                try
                {
                    if (Revisions.IsAttachedTo(writer.Connection))
                    {
                        Revisions.Checkpoint(writer.Connection);
                    }
                    else
                    {
                        Scalar(writer.Connection, null, "PRAGMA wal_checkpoint(TRUNCATE);");
                    }
                }
                finally
                {
                    Discard(writer);
                    pool.CloseIdleOf(this);
                }

                return true;
            }, CancellationToken.None).ConfigureAwait(false);
            operation?.Complete();
            WritersEvicted?.Add(1);
        }
        catch (Exception error) when (error is StorageBusyException or ObjectDisposedException)
        {
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            await RecordOperationDamageAsync(error).ConfigureAwait(false);
            logger?.ZLogWarning(error, $"Closing the writer of database {name:@Database}, the least recently used, failed");
        }
        finally
        {
            try
            {
                ReleaseWriter();
            }
            finally
            {
                if (admitted)
                {
                    io.Release();
                }
            }
        }
    }

    internal Counter<long>? WritersEvicted { get; init; }

    internal async ValueTask EndAsync(bool write, bool ownsAdmission, CopyOperation? operation, bool indexRevision = true)
    {
        try
        {
            if (write)
            {
                try
                {
                    // A BEGIN cancelled while queued never touched the writer connection. Its cleanup must
                    // release admission without queueing maintenance behind the call that blocked BEGIN.
                    if (indexRevision)
                    {
                        await IndexRevisionAsync().ConfigureAwait(false);
                    }
                }
                finally
                {
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

    internal async Task<Ulid> CopyToAsync(string path, CancellationToken cancellationToken)
    {
        const string reason = "the source file or its log reported an I/O error or corruption while copying";
        ExceptionDispatchInfo? sourceError = null;
        try
        {

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

            _ = await io.WithAdmissionAsync(async () =>
            {
                await RecordCopyFaultAsync(reason).ConfigureAwait(false);
                return true;
            }).ConfigureAwait(false);

            sourceError.Throw();
            throw;
        }

        if (Tenant is { } targetSource)
        {
            Checkpoint?.Invoke("copy.target.initializing", targetSource);
        }
        return await InitializeAsync(name, path, io, pragmas, Encryption, cancellationToken).ConfigureAwait(false);
    }

    internal string FilePath => path;

    private long _fileSeenAt;
    private long _fileSeenEpoch = -1;

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

    internal Task<T> WithWriterAsync<T>(Func<TursoSession, T> call, string holder, CancellationToken cancellationToken) =>
    WithWritersHeldAsync(() => OnWriterAsync(call, cancellationToken), cancellationToken, holder);

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

            if (writer is not null)
            {
                await WriteLane.RunAsync(() => Discard(writer), CancellationToken.None).ConfigureAwait(false);
            }
            operation?.Complete();
            throw;
        }
    }

    private const int MaxBatch = 256;
    private readonly Queue<IBatchedWrite> _queued = [];
    private readonly Lock _queueGate = new();
    private bool _batching;

    private int _batched;

    internal int BatchedWrites => Volatile.Read(ref _batched);

    internal Action? AfterGroupCommit { get; set; }

    internal Action? AfterTursoCommit { get; set; }

    internal int Statistics => Volatile.Read(ref _statistics);

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

    public async ValueTask<(T Result, CommitResult Commit)> WriteAsync<T>(
        Func<IStorageTransaction, CancellationToken, ValueTask<T>> work, Func<T, bool> keep, CancellationToken cancellationToken,
        [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
    {

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

            deferred = await io.WithAdmissionAsync(() => CommitBatchAsync(batch)).ConfigureAwait(false);
            Volatile.Write(ref _batched, 0);
        }
    }

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
                    }
                    catch (Exception error)
                    {
                        failure = error;
                    }

                    if (transaction.Aborted)
                    {

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
                    commit = await transaction.CommitAsync(CancellationToken.None).ConfigureAwait(false);
                    AfterGroupCommit?.Invoke();
                }
            }
        }
        catch (Exception error) when (error is not StorageCopyFaultedException && CopyFault is null && Ledger?.FailedClosed is null
            && !IsSystem && transaction?.Committed is { } committedAt && !transaction.ChangedReaderState)
        {

            commit = committedAt;
            try
            {
                logger?.ZLogError(error, $"A group commit of database {name:@Database} committed, but the work after its commit failed; its writes are answered as committed");
            }
            catch (Exception)
            {

            }
        }
        catch (Exception error)
        {

            batch.ForEach(write => write.Fail(error));
            return [];
        }

        if ((Ledger?.FailedClosed ?? CopyFault) is { } fault)
        {
            batch.ForEach(write => write.Fail(new StorageCopyFaultedException(fault)));
            return [];
        }

        refused.ForEach(write => write.Answer(default));
        kept.ForEach(write => write.Answer(commit));
        return deferred;
    }

    private interface IBatchedWrite
    {
        bool Cancelled { get; }

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

        public async ValueTask<bool> RunAsync(IStorageTransaction transaction)
        {

            using var scope = QosContext.Restore(_context);
            _result = await work(transaction, cancellationToken).ConfigureAwait(false);
            return keep(_result);
        }

        public void Answer(CommitResult commit) => _done.TrySetResult((_result, commit));

        public void Fail(Exception error) => _done.TrySetException(error);

        public void Cancel() => _done.TrySetCanceled(cancellationToken);
    }

    internal void Return(TursoSession session)
    {

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

    private int _statistics;

    internal void StatisticsChanged() => Interlocked.Increment(ref _statistics);

    internal void Discard(TursoSession session)
    {
        if (session == _writerSession)
        {
            _writerSession = null;
            Revisions.Detach();
            pool.ForgetWriter(this);
        }

        pool.Discard(session);
    }

    private Task ReturnAsync(TursoSession session) => io.RunAsync(() => Return(session), CancellationToken.None);

    public void Dispose()
    {
        Revisions.Dispose();
        pool.Close(this);
        pool.ForgetWriter(this);

        if (_writer.Wait(0))
        {
            ReleaseWriter();
        }
    }

    private Task<TursoSession> RentAsync(CancellationToken cancellationToken) => RentAsync(io, cancellationToken);

    private async Task<TursoSession> RentAsync(TursoRunner runner, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

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

            pool.OpenFailed(this);
            if (Closed && !cancellationToken.IsCancellationRequested)
            {

                throw new ObjectDisposedException(GetType().FullName, error);
            }

            throw;
        }

        return await runner.RunAsync(() => pool.Adopt(this, session), CancellationToken.None).ConfigureAwait(false);
    }

    internal static async Task<Ulid> InitializeAsync(
    string name, string path, TursoIoPool io, string pragmas, DatabaseKeys encryption, CancellationToken cancellationToken)
    {
        var connectionString = File.Exists(path)
            ? await encryption.ExistingAsync(path, io, cancellationToken).ConfigureAwait(false)
            : await encryption.CreateAsync(path, io, cancellationToken).ConfigureAwait(false);
        return await InitializeAsync(name, path, io, pragmas, connectionString, cancellationToken).ConfigureAwait(false);
    }

    internal static Task<Ulid> InitializeAsync(
        string name, string path, TursoIoPool io, string pragmas, string connectionString, CancellationToken cancellationToken) =>
        io.RunAsync(() =>
        {
            Ulid incarnation;
            using (var connection = new SqliteConnection(connectionString))
            {
                connection.Open();

                Scalar(connection, null, pragmas + "PRAGMA synchronous = OFF;");
                Bootstrap(name, connection, cancellationToken);

                using var transaction = connection.BeginTransaction(deferred: false);
                incarnation = RenewIncarnation(connection, transaction);
                cancellationToken.ThrowIfCancellationRequested();
                transaction.Commit();

                Scalar(connection, null, "PRAGMA wal_checkpoint(TRUNCATE);");
            }

            using var file = File.OpenHandle(path, FileMode.Open, FileAccess.ReadWrite);
            RandomAccess.FlushToDisk(file);
            return incarnation;
        }, cancellationToken);

    internal async Task BootstrapAsync(CancellationToken cancellationToken)
    {
        var session = await RentAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await session.ExecuteAsync(io, () =>
            {

                session.Settings(queryOnly: false, full: false);

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
        Scalar(connection, transaction, schema + $"PRAGMA user_version = {SchemaVersion};");
        RenewIncarnation(connection, transaction);
        cancellationToken.ThrowIfCancellationRequested();
        transaction.Commit();
    }

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

using System.Diagnostics;
using Turso.Data.Sqlite;

namespace TerraBase.Engine.Turso;

/// <summary>
/// One open connection with its per-connection settings applied and its statements prepared once,
/// reused across transactions by <see cref="TursoDatabaseStore"/>.
/// </summary>
internal sealed class TursoSession(SqliteConnection connection) : IDisposable
{
    // Keyed by statement text; the statements are constants, so this holds a handful of entries.
    private readonly Dictionary<string, SqliteCommand> _commands = [with(StringComparer.Ordinal)];

    // Statements naming a collection's table, one per collection and statement: bounded by TrimTableCommands.
    private readonly Dictionary<string, SqliteCommand> _tableCommands = [with(StringComparer.Ordinal)];

    // The most table statements an idle session keeps; past it, the next trim drops them all.
    private const int MaxTableCommands = 256;

    // The schema version every kept statement last ran against; -1 when unknown, which drops them at the next transaction.
    private long _schema = -1;

    // The transaction CheckSchema last ran in; cleared between transactions.
    private SqliteTransaction? _checked;

    // The settings this connection runs with (Settings); null until set.
    private bool? _queryOnly;
    private bool? _full;

    private QueryObservation? _queryObservation;
    private Func<long, bool>? _scanCharge;
    private Func<bool>? _rowCharge;
    private Func<object?, double>? _near;
    private Func<bool>? _stop;

    public SqliteConnection Connection => connection;

    internal global::Turso.Raw.Public.Handles.TursoDatabaseHandle Native =>
        NativeHandle?.GetValue(connection) as global::Turso.Raw.Public.Handles.TursoDatabaseHandle
        ?? throw new InvalidOperationException("the Turso driver no longer exposes its native connection handle");

    /// <summary>The store's statistics generation when this connection opened (<see cref="TursoDatabaseStore.StatisticsChanged"/>).</summary>
    public int Statistics { get; set; }

    /// <summary>Turns change capture on for this connection: every later commit lands in <c>turso_cdc</c>, after-images only.</summary>
    internal const string CaptureOn = "PRAGMA capture_data_changes_conn('after');";

    /// <summary>Turns change capture off for this connection.</summary>
    internal const string CaptureOff = "PRAGMA capture_data_changes_conn('off');";

    /// <summary>The message of the error <see cref="ThrowIfStopped"/> aborts a statement with.</summary>
    internal const string StoppedMessage = "terrabase: statement stopped";

    // Transactions serialize access to a session: a query sets what its per-row functions report to, and clears it after.
    public void ObserveQueries(QueryObservation? observation) => _queryObservation = observation;

    /// <summary>
    /// Sets what <c>tb_qos_scan</c> charges each row's stored bytes to and what <c>tb_sql_row</c> charges each evaluated row
    /// combination to: each returns true once the query's budget is spent.
    /// </summary>
    public void Charge(Func<long, bool>? scan, Func<bool>? row)
    {
        _scanCharge = scan;
        _rowCharge = row;
    }

    /// <summary>Sets what <c>tb_near</c> measures each row's document with, for the running near query only.</summary>
    public void Near(Func<object?, double>? measure) => _near = measure;

    // Registered once, when the connection opens and before any statement is prepared: a prepared statement keeps the
    // function it was prepared with, and Turso prepares every statement again after a registration.
    private void RegisterFunctions()
    {
        // The row's id argument makes Turso call it per row: it evaluates a function without arguments once.
        // Non-deterministic prevents Turso from evaluating it once per query.
        connection.CreateFunction<object?, bool>("tb_query_scan", _ =>
        {
            ThrowIfStopped();
            _queryObservation?.AddRow();
            return true;
        }, isDeterministic: false);
        // octet_length reads the stored byte size without decoding or copying the JSON body.
        connection.CreateFunction<long, long>("tb_qos_scan", size =>
        {
            if (_scanCharge?.Invoke(size) == true)
            {
                throw new OperationCanceledException(StoppedMessage);
            }

            ThrowIfStopped();
            return 1;
        });
        connection.CreateFunction<object?, long>("tb_sql_row", _ =>
        {
            if (_rowCharge?.Invoke() == true)
            {
                throw new OperationCanceledException(StoppedMessage);
            }

            ThrowIfStopped();
            return 1;
        }, isDeterministic: false);
        // Outside a near query no statement names it; a stray call sorts last, as a document without the vector does.
        connection.CreateFunction<object?, double>("tb_near",
            document => _near is { } near ? near(document) : double.PositiveInfinity, isDeterministic: false);
        // tb_geo_meters(lat, lon, centerLat, centerLon) of a within filter; +Infinity unless all four are numbers.
        connection.CreateFunction("tb_geo_meters", arguments => arguments.Length == 4
            ? GeoDistance.Meters(arguments[0], arguments[1], arguments[2], arguments[3]) : double.PositiveInfinity, isDeterministic: true);
    }

    /// <summary>
    /// Sets what the per-row functions (<c>tb_query_scan</c>, <c>tb_qos_scan</c>) check: once it returns true they fail
    /// the running statement. Native FTS also checks the shared request budget inside loading and posting traversal.
    /// </summary>
    public void StopWhen(Func<bool>? stop) => _stop = stop;

    /// <summary>Fails the running statement once <see cref="StopWhen"/>'s condition holds; call from a per-row function.</summary>
    public void ThrowIfStopped()
    {
        if (_stop?.Invoke() == true)
        {
            throw new OperationCanceledException(StoppedMessage);
        }
    }

    /// <summary>Whether <paramref name="error"/> is a statement <see cref="ThrowIfStopped"/> failed.</summary>
    public static bool Stopped(SqliteException error) => error.SqliteErrorCode == 9
        || error.Message.Contains(StoppedMessage, StringComparison.Ordinal);

    /// <summary>Whether <paramref name="error"/> is lock contention; Turso reports every local error with code 1.</summary>
    public static bool Busy(SqliteException error) => error.Message.Contains("database is locked", StringComparison.Ordinal);

    /// <summary>The bytes of a TEXT or BLOB column: Turso's reader refuses <c>GetFieldValue&lt;byte[]&gt;</c> on TEXT.</summary>
    public static byte[] Bytes(SqliteDataReader reader, int ordinal) => reader.GetValue(ordinal) switch
    {
        byte[] bytes => bytes,
        string text => System.Text.Encoding.UTF8.GetBytes(text),
        var other => throw new InvalidCastException($"column {ordinal} holds {other.GetType().Name}, not TEXT or BLOB"),
    };

    /// <summary>
    /// Per-connection settings, applied once when a connection opens (pooled sessions and the connection that
    /// creates a database). Everything else is Turso's default (WAL, 4 KiB pages, its own checkpoints, no busy timeout);
    /// the two exceptions and their reasons are in <c>docs/terrabase/sqlite-query-plans.md</c>.
    /// </summary>
    public static string Pragmas(long memoryBudget, int threads)
    {
        // A quarter of the budget for page caches, split across the connections that run at once: Turso has no
        // process-wide heap limit, so these caches are the memory bound (docs/terrabase/memory.md).
        var pageCacheBytes = memoryBudget / 4;
        var cacheKib = Math.Max(2048, pageCacheBytes / threads / 1024);
        // NORMAL until the first transaction. Every write transaction runs with FULL, so its log is synced before it is
        // answered (0003 as amended, 0012); the writer keeps FULL after it, also for its work outside transactions (backup tap).
        return string.Create(System.Globalization.CultureInfo.InvariantCulture, $"""
            PRAGMA synchronous = NORMAL;
            PRAGMA cache_size = -{cacheKib};
            PRAGMA foreign_keys = ON;
            """);
    }

    /// <summary>
    /// Opens a connection on the <see cref="TursoIoPool"/> and applies <paramref name="pragmas"/>, its
    /// per-connection settings, under the cancellable busy handling of <see cref="ExecuteAsync"/>.
    /// </summary>
    public static async Task<TursoSession> OpenAsync(TursoRunner io, string connectionString, string pragmas, CancellationToken cancellationToken, TursoDatabaseStore? store = null)
    {
        var connection = new SqliteConnection(connectionString)
        {
            CloseRefusal = store is null ? null : () => store.CloseRefusal,
        };
        var session = new TursoSession(connection);
        var opening = false;
        try
        {
            await io.RunAsync(() =>
            {
                opening = true;
                OpenExisting(connection, new SqliteConnectionStringBuilder(connectionString).DataSource);
            }, cancellationToken).ConfigureAwait(false);
            await session.ExecuteAsync(io, () =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = pragmas;
                command.ExecuteNonQuery();
                session.RegisterFunctions();
                return true;
            }, cancellationToken).ConfigureAwait(false);
            return session;
        }
        catch (Exception error)
        {
            if (TursoStorageTransaction.Damaged(error))
            {
                store?.NoteCopyFault("the file or its log reported damage while opening a session");
            }
            if (Volatile.Read(ref opening))
            {
                await io.RunAsync(session.Dispose, CancellationToken.None).ConfigureAwait(false);
            }
            else
            {
                // Cancelled while queued: never opened, so closing it does no I/O and must not wait for a worker.
                session.Dispose();
            }

            throw;
        }
    }

    /// <summary>
    /// Opens <paramref name="connection"/> on the existing database file <paramref name="path"/>. Turso ignores
    /// <c>Mode=ReadWrite</c> and creates a missing file, but only <c>InitializeAsync</c> may create one: a store opened
    /// as its database is deleted must not leave an empty file behind. Every database file holds at least its schema, so
    /// an empty one is such a file.
    /// </summary>
    public static void OpenExisting(System.Data.Common.DbConnection connection, string path)
    {
        if (!File.Exists(path))
        {
            throw new SqliteException("unable to open database file: it does not exist", 14);
        }

        connection.Open();
        if (new FileInfo(path).Length == 0)
        {
            connection.Dispose();
            File.Delete(path);
            File.Delete(path + "-wal");
            throw new SqliteException("unable to open database file: it does not exist", 14);
        }
    }

    // The provider has no true async I/O, so each attempt runs on a pool worker. An attempt fails at once on lock
    // contention (Turso's default busy timeout is 0, and one writer per file makes contention rare); between attempts the
    // worker is free and cancellation is checked,
    // within the original overall timeout. Turso cannot interrupt a running statement, so cancellation is checked only
    // between attempts. Retried operations must be reads or atomic work that rolls back before propagating a busy error.
    public async Task<T> ExecuteAsync<T>(TursoRunner io, Func<T> execute, CancellationToken cancellationToken)
    {
        var timeout = connection.DefaultTimeout;
        var started = Stopwatch.GetTimestamp();
        while (true)
        {
            try
            {
                return await io.RunAsync(() =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return execute();
                }, cancellationToken).ConfigureAwait(false);
            }
            catch (SqliteException error) when (Busy(error) && (timeout == 0 || Stopwatch.GetElapsedTime(started).TotalSeconds < timeout))
            {
                await Task.Delay(50, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Returns the prepared command for <paramref name="sql"/>, bound to <paramref name="transaction"/>
    /// and without parameters. The provider keeps a command's statements prepared while its text is unchanged.
    /// </summary>
    public SqliteCommand Command(string sql, SqliteTransaction transaction) => Kept(_commands, sql, transaction);

    /// <summary>
    /// The kept command for <paramref name="sql"/>, run outside any transaction right after <paramref name="transaction"/>
    /// committed on this connection, or null when that transaction did not check the schema (<see cref="CheckSchema"/>). No
    /// other connection commits DDL while the database's writer is held, so what the check kept is still current.
    /// </summary>
    public SqliteCommand? AfterCommit(string sql, SqliteTransaction transaction)
    {
        if (!ReferenceEquals(_checked, transaction))
        {
            return null;
        }

        if (!_commands.TryGetValue(sql, out var command))
        {
            command = connection.CreateCommand();
            command.CommandText = sql;
            command.ReuseStatement = true;
            _commands.Add(sql, command);
        }

        command.Transaction = null;
        command.Parameters.Clear();
        return command;
    }

    /// <summary>
    /// Outside any transaction, sets what the next transaction needs: read-only (<c>query_only</c>, so BEGIN DEFERRED cannot
    /// upgrade to a write) and whether its commit syncs the log (<c>synchronous = FULL</c>, decision 0010). Only a change
    /// runs: Turso makes every prepared statement prepare again after any such setting, so a change also drops the kept ones.
    /// </summary>
    public void Settings(bool queryOnly, bool full)
    {
        if (_queryOnly == queryOnly && _full == full)
        {
            return;
        }

        Drop(_commands);
        Drop(_tableCommands);
        _queryOnly = null;
        _full = null;
        using var command = connection.CreateCommand();
        command.CommandText = (queryOnly ? "PRAGMA query_only = 1;" : "PRAGMA query_only = 0;")
            + (full ? "PRAGMA synchronous = FULL;" : "PRAGMA synchronous = NORMAL;");
        command.ExecuteNonQuery();
        _queryOnly = queryOnly;
        _full = full;
    }

    /// <summary><see cref="Command"/> for a statement naming one collection's table.</summary>
    public SqliteCommand TableCommand(string sql, SqliteTransaction transaction) => Kept(_tableCommands, sql, transaction);

    /// <summary>
    /// Between transactions: releases the table statements' parameters, so an idle session retains no bound JSON (a reset
    /// statement holds no bindings), and drops the statements once a session holds more than it should keep.
    /// </summary>
    public void TrimTableCommands()
    {
        _checked = null;
        if (_tableCommands.Count > MaxTableCommands)
        {
            Drop(_tableCommands);
            return;
        }

        foreach (var command in _tableCommands.Values)
        {
            command.Parameters.Clear();
        }
    }

    // Before a transaction's first kept statement, which is where its deferred snapshot would start anyway: drops the kept
    // statements when the schema changed since they last ran. Turso 0.8.2 cannot run a statement prepared against another
    // schema inside an explicit transaction once another connection's DDL has committed ("Database schema changed", the
    // transaction unusable), so a kept statement must never be older than the snapshot. A fresh statement is compiled
    // against the connection's own schema.
    private void CheckSchema(SqliteTransaction transaction)
    {
        _checked = transaction;
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "PRAGMA schema_version;";
        var schema = Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        if (schema != _schema)
        {
            Drop(_commands);
            Drop(_tableCommands);
            _schema = schema;
        }
    }

    /// <summary>
    /// After a write transaction that did not commit: its own DDL may have recompiled kept statements against a schema
    /// that was rolled back, so the next transaction drops them.
    /// </summary>
    public void ForgetSchema() => _schema = -1;

    private static void Drop(Dictionary<string, SqliteCommand> commands)
    {
        foreach (var command in commands.Values)
        {
            command.Dispose();
        }

        commands.Clear();
    }

    // Each command keeps its prepared statement across executions and transactions (the driver's ReuseStatement): preparing
    // was the largest CPU cost of a point read. CheckSchema and Settings drop them whenever Turso would prepare them again.
    private SqliteCommand Kept(Dictionary<string, SqliteCommand> commands, string sql, SqliteTransaction transaction)
    {
        if (!ReferenceEquals(_checked, transaction))
        {
            CheckSchema(transaction);
        }

        if (!commands.TryGetValue(sql, out var command))
        {
            command = connection.CreateCommand();
            command.CommandText = sql;
            command.ReuseStatement = true;
            commands.Add(sql, command);
        }

        command.Transaction = transaction;
        command.Parameters.Clear();
        return command;
    }

    // Turso's provider keeps its native connection handle private; Dispose closes it even when the provider's close throws.
    private static readonly System.Reflection.FieldInfo? NativeHandle =
        typeof(SqliteConnection).GetField("_database", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

    /// <summary>
    /// Releases a quiescent connection without graceful close or checkpoint. The native handle is closed even when
    /// disposing a command or the provider's own close throws, so the session holds no file open afterward.
    /// </summary>
    public void Dispose()
    {
        var handle = NativeHandle?.GetValue(connection) as global::Turso.Raw.Public.Handles.TursoDatabaseHandle;
        try
        {
            // Idle eviction must not mutate storage after its operation's durable intent has retired.
            handle?.Deinitialize();
            foreach (var command in _commands.Values.Concat(_tableCommands.Values))
            {
                command.Dispose();
            }
        }
        finally
        {
            try
            {
                connection.Dispose();
            }
            finally
            {
                // A no-op when the provider already closed it.
                handle?.Dispose();
            }
        }
    }
}

using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Data.Common;
using Turso.Data.Sqlite;
using Turso.Raw.Public.Handles;

namespace TerraBase.Engine.Turso;

internal sealed class TursoSession(SqliteConnection connection) : IDisposable
{
    // Keyed by statement text; the statements are constants, so this holds a handful of entries.
    private readonly Dictionary<string, SqliteCommand> _commands = [with(StringComparer.Ordinal)];

    // Statements naming one table, one per table and statement: bounded by TrimTableCommands.
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

    private Func<long, bool>? _scanCharge;
    private Func<bool>? _rowCharge;
    private Func<bool>? _stop;

    public SqliteConnection Connection => connection;

    internal TursoDatabaseHandle Native =>
        NativeHandle?.GetValue(connection) as TursoDatabaseHandle
        ?? throw new InvalidOperationException("the Turso driver no longer exposes its native connection handle");

    public int Statistics { get; set; }

    internal const string CaptureOn = "PRAGMA capture_data_changes_conn('full');";

    internal const string CaptureOff = "PRAGMA capture_data_changes_conn('off');";

    internal const string StoppedMessage = "terrabase: statement stopped";

    public void Charge(Func<long, bool>? scan, Func<bool>? row)
    {
        _scanCharge = scan;
        _rowCharge = row;
    }

    // Registered once, when the connection opens and before any statement is prepared: a prepared statement keeps the
    // function it was prepared with, and Turso prepares every statement again after a registration.
    private void RegisterFunctions()
    {
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
    }

    public void StopWhen(Func<bool>? stop) => _stop = stop;

    public void ThrowIfStopped()
    {
        if (_stop?.Invoke() == true)
        {
            throw new OperationCanceledException(StoppedMessage);
        }
    }

    public static bool Stopped(SqliteException error) => error.SqliteErrorCode == 9
        || error.Message.Contains(StoppedMessage, StringComparison.Ordinal);

    public static bool Busy(SqliteException error) => error.Message.Contains("database is locked", StringComparison.Ordinal);

    public static byte[] Bytes(SqliteDataReader reader, int ordinal) => reader.GetValue(ordinal) switch
    {
        byte[] bytes => bytes,
        string text => Encoding.UTF8.GetBytes(text),
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
        return string.Create(CultureInfo.InvariantCulture, $"""
            PRAGMA synchronous = NORMAL;
            PRAGMA cache_size = -{cacheKib};
            PRAGMA foreign_keys = ON;
            """);
    }

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
                if (store is not null)
                {
                    connection.UseManualWal();
                }
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

    public static void OpenExisting(DbConnection connection, string path)
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

    public SqliteCommand Command(string sql, SqliteTransaction transaction) => Kept(_commands, sql, transaction);

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

    public SqliteCommand TableCommand(string sql, SqliteTransaction transaction) => Kept(_tableCommands, sql, transaction);

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
        var schema = Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        if (schema != _schema)
        {
            Drop(_commands);
            Drop(_tableCommands);
            _schema = schema;
        }
    }

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
    private static readonly FieldInfo? NativeHandle =
        typeof(SqliteConnection).GetField("_database", BindingFlags.NonPublic | BindingFlags.Instance);

    public void Dispose()
    {
        var handle = NativeHandle?.GetValue(connection) as TursoDatabaseHandle;
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

using System.Diagnostics;
using Turso.Data.Sqlite;

namespace TerraBase.Engine.Sqlite;

/// <summary>
/// One open connection with its per-connection settings applied and its statements prepared once,
/// reused across transactions by <see cref="SqliteDatabaseStore"/>.
/// </summary>
internal sealed class SqliteSession(SqliteConnection connection) : IDisposable
{
    // Keyed by statement text; the statements are constants, so this holds a handful of entries.
    private readonly Dictionary<string, SqliteCommand> _commands = [with(StringComparer.Ordinal)];

    private QueryObservation? _queryObservation;
    private bool _scanRegistered;
    private Func<bool>? _stop;

    public SqliteConnection Connection => connection;

    /// <summary>The store's statistics generation when this connection opened (<see cref="SqliteDatabaseStore.StatisticsChanged"/>).</summary>
    public int Statistics { get; set; }

    /// <summary>Turns change capture on for this connection: every later commit lands in <c>turso_cdc</c>, after-images only.</summary>
    internal const string CaptureOn = "PRAGMA capture_data_changes_conn('after');";

    /// <summary>Turns change capture off for this connection.</summary>
    internal const string CaptureOff = "PRAGMA capture_data_changes_conn('off');";

    /// <summary>The message of the error <see cref="ThrowIfStopped"/> aborts a statement with.</summary>
    internal const string StoppedMessage = "terrabase: statement stopped";

    // Transactions serialize access to a session. Keep the function registered while pooled, but release the
    // execution's observation on completion. Non-deterministic prevents SQLite from evaluating it once per query.
    public void ObserveQueries(QueryObservation? observation)
    {
        if (observation is not null && !_scanRegistered)
        {
            // The row's id argument makes Turso call it per row: it evaluates a function without arguments once.
            connection.CreateFunction<object?, bool>("tb_query_scan", _ =>
            {
                ThrowIfStopped();
                _queryObservation?.AddRow();
                return true;
            }, isDeterministic: false);
            _scanRegistered = true;
        }

        _queryObservation = observation;
    }

    /// <summary>
    /// Sets what the per-row functions (<c>tb_query_scan</c>, <c>tb_qos_scan</c>) check: once it returns true they fail
    /// the running statement. Turso has no <c>sqlite3_interrupt</c> or progress handler, so this is the only way to stop a
    /// statement before it finishes (docs/terrabase/turso-spike.md).
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
    public static bool Stopped(SqliteException error) => error.Message.Contains(StoppedMessage, StringComparison.Ordinal);

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
        // NORMAL is the pooled default between transactions and for writer work outside them (backup tap). Every write
        // transaction switches to FULL for its commit, so its log is synced before it is answered (0003 as amended, 0012).
        return string.Create(System.Globalization.CultureInfo.InvariantCulture, $"""
            PRAGMA synchronous = NORMAL;
            PRAGMA cache_size = -{cacheKib};
            """);
    }

    /// <summary>
    /// Opens a connection on the <see cref="SqliteIoPool"/> and applies <paramref name="pragmas"/>, its
    /// per-connection settings, under the cancellable busy handling of <see cref="ExecuteAsync"/>.
    /// </summary>
    public static async Task<SqliteSession> OpenAsync(SqliteRunner io, string connectionString, string pragmas, CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(connectionString);
        var opening = false;
        try
        {
            await io.RunAsync(() =>
            {
                opening = true;
                OpenExisting(connection, new SqliteConnectionStringBuilder(connectionString).DataSource);
            }, cancellationToken).ConfigureAwait(false);
            var session = new SqliteSession(connection);
            await session.ExecuteAsync(io, () =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = pragmas;
                return command.ExecuteNonQuery();
            }, cancellationToken).ConfigureAwait(false);
            return session;
        }
        catch
        {
            if (Volatile.Read(ref opening))
            {
                await io.RunAsync(connection.Dispose, CancellationToken.None).ConfigureAwait(false);
            }
            else
            {
                // Cancelled while queued: never opened, so closing it does no I/O and must not wait for a worker.
                connection.Dispose();
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
    public async Task<T> ExecuteAsync<T>(SqliteRunner io, Func<T> execute, CancellationToken cancellationToken)
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
    public SqliteCommand Command(string sql, SqliteTransaction transaction)
    {
        if (!_commands.TryGetValue(sql, out var command))
        {
            command = connection.CreateCommand();
            command.CommandText = sql;
            _commands.Add(sql, command);
        }

        command.Transaction = transaction;
        command.Parameters.Clear();
        return command;
    }

    // Turso's provider keeps its native connection handle private; Dispose closes it even when the provider's close throws.
    private static readonly System.Reflection.FieldInfo? NativeHandle =
        typeof(SqliteConnection).GetField("_database", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

    /// <summary>
    /// Closes the connection. The native handle is closed even when disposing a command or the provider's own close
    /// throws, so once this returns, or throws, the session holds no file open.
    /// </summary>
    public void Dispose()
    {
        var handle = NativeHandle?.GetValue(connection) as System.Runtime.InteropServices.SafeHandle;
        try
        {
            foreach (var command in _commands.Values)
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

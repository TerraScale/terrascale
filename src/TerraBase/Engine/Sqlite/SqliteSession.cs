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

    public SqliteConnection Connection => connection;

    // Transactions serialize access to a session. Keep the function registered while pooled, but release the
    // execution's observation on completion. Non-deterministic prevents SQLite from evaluating it once per query.
    public void ObserveQueries(QueryObservation? observation)
    {
        if (observation is not null && !_scanRegistered)
        {
            connection.CreateFunction("tb_query_scan", () =>
            {
                _queryObservation?.AddRow();
                return true;
            }, isDeterministic: false);
            _scanRegistered = true;
        }

        _queryObservation = observation;
    }

    /// <summary>
    /// Per-connection settings, applied once when a connection opens (pooled sessions and the connection that
    /// creates a database). The reasons, and how the page cache and the memory map are sized from
    /// <c>memory-budget</c>, are in <c>docs/sqlite-query-plans.md</c>; <c>synchronous = NORMAL</c> is decision 0003.
    /// With <paramref name="walTaps"/> continuous backup checkpoints the log instead of SQLite (<see cref="SqliteWalTap"/>).
    /// </summary>
    public static string Pragmas(long memoryBudget, int threads, bool walTaps = false)
    {
        // A quarter of the budget for page caches, split across the connections that run at once. Idle
        // connections keep theirs, so LimitHeap holds SQLite's whole heap to that quarter.
        var pageCacheBytes = memoryBudget / 4;
        var cacheKib = Math.Max(2048, pageCacheBytes / threads / 1024);
        // Address space backed by the OS page cache, not the heap; SQLite caps it at its compile-time maximum.
        var mmapBytes = Math.Max(256L * 1024 * 1024, memoryBudget);
        return string.Create(System.Globalization.CultureInfo.InvariantCulture, $"""
            PRAGMA synchronous = NORMAL;
            PRAGMA temp_store = FILE;
            PRAGMA temp.cache_size = -1024;
            PRAGMA mmap_size = {mmapBytes};
            PRAGMA cache_size = -{cacheKib};
            {(walTaps ? "PRAGMA wal_autocheckpoint = 0;" : "")}
            """);
    }

    /// <summary>
    /// Loads the native library. turso-spike: SQLite held its heap to a quarter of the memory budget
    /// (<c>soft_heap_limit</c>); Turso has no equivalent.
    /// </summary>
    public static void LimitHeap()
    {
        // Loads SQLite's native library, as the first connection would.
        using (new SqliteConnection())
        {
        }

        // turso-spike: Turso has no process heap limit (TursoShim).
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
                connection.Open();
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

    // Microsoft.Data.Sqlite has no true async I/O, so each attempt runs on a pool worker. The provider retries
    // lock contention for at most one second per attempt with short native busy backoff; between attempts the
    // worker is free and cancellation is checked, within the original overall timeout. Cancellation interrupts
    // only the attempt it owns, and never abandons it while SQLite still uses the connection. Retried
    // operations must be reads or atomic work that rolls back before propagating a busy error.
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
                    connection.DefaultTimeout = 1;
                    TursoShim.BusyTimeout(connection, 100);
                    try
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        return execute();
                    }
                    finally
                    {
                        connection.DefaultTimeout = timeout;
                        TursoShim.BusyTimeout(connection, 30000);
                    }
                }, cancellationToken).ConfigureAwait(false);
            }
            catch (SqliteException error) when (error.SqliteErrorCode == TursoShim.SQLITE_INTERRUPT && cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException("the storage operation was cancelled", error, cancellationToken);
            }
            catch (SqliteException error) when (TursoShim.IsBusy(error)
                && (timeout == 0 || Stopwatch.GetElapsedTime(started).TotalSeconds < timeout))
            {
                await Task.Delay(50, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Returns the prepared command for <paramref name="sql"/>, bound to <paramref name="transaction"/>
    /// and without parameters. Microsoft.Data.Sqlite keeps a command's statements prepared while its text is unchanged.
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

    /// <summary>
    /// Closes the connection. The native database handle is closed even when disposing a command or the provider's
    /// own close throws, so once this returns, or throws, the session holds no file open.
    /// </summary>
    public void Dispose()
    {
        try
        {
            foreach (var command in _commands.Values)
            {
                command.Dispose();
            }
        }
        finally
        {
            connection.Dispose();
        }
    }
}

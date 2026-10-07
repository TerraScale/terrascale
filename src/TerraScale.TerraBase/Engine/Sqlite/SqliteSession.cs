using System.Diagnostics;
using Microsoft.Data.Sqlite;
using SQLitePCL;

namespace TerraBase.Engine.Sqlite;

/// <summary>
/// One open connection with its per-connection settings applied and its statements prepared once,
/// reused across transactions by <see cref="SqliteDatabaseStore"/>.
/// </summary>
internal sealed class SqliteSession(SqliteConnection connection) : IDisposable
{
    private static readonly Lock HeapLimitGate = new();

    // Keyed by statement text; the statements are constants, so this holds a handful of entries.
    private readonly Dictionary<string, SqliteCommand> _commands = [with(StringComparer.Ordinal)];

    public SqliteConnection Connection => connection;

    /// <summary>
    /// Per-connection settings, applied once when a connection opens (pooled sessions and the connection that
    /// creates a database). The reasons, and how the page cache and the memory map are sized from
    /// <c>memory-budget</c>, are in <c>docs/sqlite-query-plans.md</c>; <c>synchronous = NORMAL</c> is decision 0003.
    /// </summary>
    public static string Pragmas(long memoryBudget, int threads)
    {
        // A quarter of the budget for page caches, split across the connections that run at once. Idle
        // connections keep theirs, so LimitHeap holds SQLite's whole heap to that quarter.
        var pageCacheBytes = memoryBudget / 4;
        var cacheKib = Math.Max(2048, pageCacheBytes / threads / 1024);
        // Address space backed by the OS page cache, not the heap; SQLite caps it at its compile-time maximum.
        var mmapBytes = Math.Max(256L * 1024 * 1024, memoryBudget);
        return string.Create(System.Globalization.CultureInfo.InvariantCulture, $"""
            PRAGMA synchronous = NORMAL;
            PRAGMA mmap_size = {mmapBytes};
            PRAGMA cache_size = -{cacheKib};
            """);
    }

    /// <summary>
    /// Holds SQLite's heap, process-wide, to a quarter of <paramref name="memoryBudget"/> (<c>soft_heap_limit</c>):
    /// SQLite frees page cache to stay under it. The limit is global to the process, so another engine in it
    /// (tests run several) only ever raises it, never lowers it below what an engine already set.
    /// </summary>
    public static void LimitHeap(long memoryBudget)
    {
        // Loads SQLite's native library, as the first connection would.
        using (new SqliteConnection())
        {
        }

        var limit = memoryBudget / 4;
        // Engines starting at once must not interleave the read and the write and lower the limit.
        lock (HeapLimitGate)
        {
            // A negative argument reads the current limit; 0 means none.
            var current = raw.sqlite3_soft_heap_limit64(-1);
            if (current == 0 || current < limit)
            {
                raw.sqlite3_soft_heap_limit64(limit);
            }
        }
    }

    /// <summary>
    /// Opens a connection on the <see cref="SqliteIoPool"/> and applies <paramref name="pragmas"/>, its
    /// per-connection settings, under the cancellable busy handling of <see cref="ExecuteAsync"/>.
    /// </summary>
    public static async Task<SqliteSession> OpenAsync(SqliteIoPool io, string connectionString, string pragmas, CancellationToken cancellationToken)
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
    public async Task<T> ExecuteAsync<T>(SqliteIoPool io, Func<T> execute, CancellationToken cancellationToken)
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
                    raw.sqlite3_busy_timeout(connection.Handle, 100);
                    try
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        using var interrupt = cancellationToken.Register(() => raw.sqlite3_interrupt(connection.Handle));
                        return execute();
                    }
                    finally
                    {
                        connection.DefaultTimeout = timeout;
                        raw.sqlite3_busy_timeout(connection.Handle, 30000);
                    }
                }, cancellationToken).ConfigureAwait(false);
            }
            catch (SqliteException error) when (error.SqliteErrorCode == raw.SQLITE_INTERRUPT && cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException("the storage operation was cancelled", error, cancellationToken);
            }
            catch (SqliteException error) when (error.SqliteErrorCode is raw.SQLITE_BUSY or raw.SQLITE_LOCKED
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
        var handle = connection.Handle;
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

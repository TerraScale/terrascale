using System.Diagnostics;
using Microsoft.Data.Sqlite;
using SQLitePCL;

namespace TerraScale.TerraBase.Engine.Sqlite;

/// <summary>
/// One open connection with its per-connection settings applied and its statements prepared once,
/// reused across transactions by <see cref="SqliteDatabaseStore"/>.
/// </summary>
internal sealed class SqliteSession(SqliteConnection connection) : IDisposable
{
    /// <summary>
    /// Per-connection settings, applied once when a connection opens. The reasons are in
    /// <c>docs/sqlite-query-plans.md</c>; <c>synchronous = NORMAL</c> is decision 0003.
    /// </summary>
    internal const string Pragmas = """
        PRAGMA synchronous = NORMAL;
        PRAGMA mmap_size = 268435456;
        """;

    // Keyed by statement text; the statements are constants, so this holds a handful of entries.
    private readonly Dictionary<string, SqliteCommand> _commands = [with(StringComparer.Ordinal)];

    public SqliteConnection Connection => connection;

    public static async Task<SqliteSession> OpenAsync(string connectionString, CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            var session = new SqliteSession(connection);
            await session.ExecuteAsync(async () =>
            {
                var command = connection.CreateCommand();
                await using (command.ConfigureAwait(false))
                {
                    command.CommandText = Pragmas;
                    return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }
            }, cancellationToken).ConfigureAwait(false);
            return session;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    // This provider's async APIs run synchronously and only check cancellation before starting.
    // Let it retry lock contention for at most one second at a time; we retry between attempts
    // with cancellation, keeping the original overall timeout and short native busy backoff.
    // Never abandon an operation or return its session while SQLite still owns it. Retried
    // operations must be reads or atomic work that rolls back before propagating a busy error.
    public async ValueTask<T> ExecuteAsync<T>(Func<ValueTask<T>> execute, CancellationToken cancellationToken)
    {
        var timeout = connection.DefaultTimeout;
        var started = Stopwatch.GetTimestamp();
        connection.DefaultTimeout = 1;
        raw.sqlite3_busy_timeout(connection.Handle, 100);
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var interrupt = cancellationToken.Register(() => raw.sqlite3_interrupt(connection.Handle));
                try
                {
                    return await execute().ConfigureAwait(false);
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
        finally
        {
            connection.DefaultTimeout = timeout;
            raw.sqlite3_busy_timeout(connection.Handle, 30000);
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

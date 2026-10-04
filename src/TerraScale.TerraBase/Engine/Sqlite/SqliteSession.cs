using Microsoft.Data.Sqlite;

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
        PRAGMA busy_timeout = 30000;
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
            var command = connection.CreateCommand();
            await using (command.ConfigureAwait(false))
            {
                command.CommandText = Pragmas;
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            return new SqliteSession(connection);
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
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

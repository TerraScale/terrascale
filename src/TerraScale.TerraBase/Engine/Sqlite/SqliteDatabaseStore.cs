using Microsoft.Data.Sqlite;

namespace TerraScale.TerraBase.Engine.Sqlite;

internal sealed class SqliteDatabaseStore(DatabaseName name, string path) : IDatabaseStore
{
    /// <summary>On-disk schema version, stored in <c>PRAGMA user_version</c>.</summary>
    private const int SchemaVersion = 1;

    private readonly string _connectionString = new SqliteConnectionStringBuilder
    {
        DataSource = path,
        Mode = SqliteOpenMode.ReadWriteCreate,
        Pooling = true,
        // Seconds a statement waits on a locked database before failing.
        DefaultTimeout = 30,
    }.ToString();

    public DatabaseName Name => name;

    public async ValueTask<IStorageTransaction> BeginTransactionAsync(bool write, CancellationToken cancellationToken)
    {
        var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Writers take the lock up front (BEGIN IMMEDIATE) so they wait instead of failing on upgrade.
            return new SqliteStorageTransaction(connection, connection.BeginTransaction(deferred: !write));
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    internal async Task MigrateAsync(CancellationToken cancellationToken)
    {
        var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            var version = Convert.ToInt32(await ScalarAsync(connection, "PRAGMA user_version;", cancellationToken).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);
            if (version > SchemaVersion)
            {
                throw new InvalidOperationException($"database '{name}' has schema version {version}; this build supports up to {SchemaVersion}");
            }

            if (version == SchemaVersion)
            {
                return;
            }

            await ScalarAsync(connection, """
                PRAGMA journal_mode = WAL;
                CREATE TABLE IF NOT EXISTS documents (
                    collection TEXT NOT NULL,
                    id TEXT NOT NULL,
                    version INTEGER NOT NULL,
                    body TEXT NOT NULL,
                    PRIMARY KEY (collection, id)
                ) WITHOUT ROWID;
                PRAGMA user_version = 1;
                """, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private static async Task<object?> ScalarAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        await using (command.ConfigureAwait(false))
        {
            command.CommandText = sql;
            return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}

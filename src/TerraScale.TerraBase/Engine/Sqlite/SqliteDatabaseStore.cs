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

    /// <summary>
    /// Creates and migrates a new database file at <paramref name="path"/> on an unpooled
    /// connection, so no handle stays open once this returns and the file can be moved.
    /// </summary>
    internal static async Task InitializeAsync(DatabaseName name, string path, CancellationToken cancellationToken)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString();
        var connection = new SqliteConnection(connectionString);
        await using (connection.ConfigureAwait(false))
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await new SqliteDatabaseStore(name, path).MigrateAsync(connection, cancellationToken).ConfigureAwait(false);
        }
    }

    internal async Task MigrateAsync(CancellationToken cancellationToken)
    {
        var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            await MigrateAsync(connection, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task MigrateAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        if (await IsCurrentAsync(connection, transaction: null, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        // journal_mode cannot change inside a transaction; it is persistent and idempotent.
        await ScalarAsync(connection, null, "PRAGMA journal_mode = WAL;", cancellationToken).ConfigureAwait(false);
        var transaction = connection.BeginTransaction(deferred: false);
        await using (transaction.ConfigureAwait(false))
        {
            // Another process or caller may have migrated while this one waited for the lock.
            if (!await IsCurrentAsync(connection, transaction, cancellationToken).ConfigureAwait(false))
            {
                await ScalarAsync(connection, transaction, """
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

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    // True when the schema is current; throws for a newer, unsupported schema.
    private async Task<bool> IsCurrentAsync(SqliteConnection connection, SqliteTransaction? transaction, CancellationToken cancellationToken)
    {
        var version = Convert.ToInt32(
            await ScalarAsync(connection, transaction, "PRAGMA user_version;", cancellationToken).ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture);
        return version > SchemaVersion
            ? throw new InvalidOperationException($"database '{name}' has schema version {version}; this build supports up to {SchemaVersion}")
            : version == SchemaVersion;
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private static async Task<object?> ScalarAsync(
        SqliteConnection connection, SqliteTransaction? transaction, string sql, CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        await using (command.ConfigureAwait(false))
        {
            command.Transaction = transaction;
            command.CommandText = sql;
            return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}

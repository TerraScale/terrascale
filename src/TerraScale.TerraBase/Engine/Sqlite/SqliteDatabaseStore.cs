using Microsoft.Data.Sqlite;

namespace TerraScale.TerraBase.Engine.Sqlite;

internal sealed class SqliteDatabaseStore(DatabaseName name, string path, SqliteSessionPool pool) : IDatabaseStore, IDisposable
{
    /// <summary>On-disk schema version, stored in <c>PRAGMA user_version</c>.</summary>
    private const int SchemaVersion = 1;

    private readonly string _connectionString = new SqliteConnectionStringBuilder
    {
        DataSource = path,
        // Never recreate a deleted database: only InitializeAsync creates files.
        Mode = SqliteOpenMode.ReadWrite,
        // This store pools sessions itself, so prepared statements survive between transactions.
        Pooling = false,
        // Seconds a statement waits on a locked database before failing.
        DefaultTimeout = 30,
    }.ToString();

    public DatabaseName Name => name;

    /// <summary>Set once the database is deleted or the engine shuts down; guarded by <see cref="SqliteSessionPool"/>.</summary>
    internal bool Closed { get; set; }

    /// <summary>Idle sessions of this database, for tests.</summary>
    internal int IdleCount => pool.IdleCountOf(this);

    public async ValueTask<IStorageTransaction> BeginTransactionAsync(bool write, CancellationToken cancellationToken)
    {
        var session = await RentAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Writers take the lock up front (BEGIN IMMEDIATE) so they wait instead of failing on upgrade.
            return new SqliteStorageTransaction(this, session, session.Connection.BeginTransaction(deferred: !write));
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    /// <summary>Writes a consistent snapshot of this database to a new file at <paramref name="path"/>.</summary>
    internal async Task CopyToAsync(string path, CancellationToken cancellationToken)
    {
        var session = await RentAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var command = session.Connection.CreateCommand();
            await using (command.ConfigureAwait(false))
            {
                command.CommandText = "VACUUM INTO $path;";
                command.Parameters.AddWithValue("$path", path);
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            session.Dispose();
            throw;
        }

        Return(session);
        // InitializeAsync switches the copy to WAL and refuses an unsupported schema version.
        await InitializeAsync(name, path, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Takes back a session whose work has ended; the pool keeps it idle or closes it.</summary>
    internal void Return(SqliteSession session) => pool.Return(this, session);

    /// <summary>
    /// Closes idle sessions and refuses new ones; a rented session is closed when it is returned.
    /// The engine calls this when the database is deleted and at shutdown.
    /// </summary>
    public void Dispose() => pool.Close(this);

    private async Task<SqliteSession> RentAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // A deleted or shut-down database hands out no session.
        if (pool.Rent(this) is { } idle)
        {
            return idle;
        }

        var session = await SqliteSession.OpenAsync(_connectionString, cancellationToken).ConfigureAwait(false);
        return pool.Adopt(this, session);
    }

    /// <summary>
    /// Creates (or adopts a copied) and migrates a new database file at <paramref name="path"/> on an unpooled
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
            // A copy made by VACUUM INTO may already be current, so set WAL here and not only in a migration.
            // The creating connection uses the same settings as every pooled one.
            await ScalarAsync(connection, null, "PRAGMA journal_mode = WAL;" + SqliteSession.Pragmas, cancellationToken).ConfigureAwait(false);
            await MigrateAsync(name, connection, cancellationToken).ConfigureAwait(false);
        }
    }

    internal async Task MigrateAsync(CancellationToken cancellationToken)
    {
        var session = await RentAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await MigrateAsync(name, session.Connection, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            session.Dispose();
            throw;
        }

        Return(session);
    }

    private static async Task MigrateAsync(DatabaseName name, SqliteConnection connection, CancellationToken cancellationToken)
    {
        if (await IsCurrentAsync(name, connection, transaction: null, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        // journal_mode cannot change inside a transaction; it is persistent and idempotent.
        await ScalarAsync(connection, null, "PRAGMA journal_mode = WAL;", cancellationToken).ConfigureAwait(false);
        var transaction = connection.BeginTransaction(deferred: false);
        await using (transaction.ConfigureAwait(false))
        {
            // Another process or caller may have migrated while this one waited for the lock.
            if (!await IsCurrentAsync(name, connection, transaction, cancellationToken).ConfigureAwait(false))
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
    private static async Task<bool> IsCurrentAsync(
        DatabaseName name, SqliteConnection connection, SqliteTransaction? transaction, CancellationToken cancellationToken)
    {
        var version = Convert.ToInt32(
            await ScalarAsync(connection, transaction, "PRAGMA user_version;", cancellationToken).ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture);
        return version > SchemaVersion
            ? throw new InvalidOperationException($"database '{name}' has schema version {version}; this build supports up to {SchemaVersion}")
            : version == SchemaVersion;
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

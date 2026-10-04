using Microsoft.Data.Sqlite;

namespace TerraScale.TerraBase.Engine.Sqlite;

/// <param name="name">Names the database in error messages: its id, or <c>_system</c>.</param>
/// <param name="path">The database file.</param>
/// <param name="pool">Idle sessions, shared by every database of the engine.</param>
internal sealed class SqliteDatabaseStore(string name, string path, SqliteSessionPool pool) : IDatabaseStore, IDisposable
{
    /// <summary>On-disk schema version, stored in <c>PRAGMA user_version</c>.</summary>
    private const int SchemaVersion = 5;

    // Migrations[n] moves user_version n to n + 1.
    private static readonly string[] Migrations =
    [
        """
        CREATE TABLE IF NOT EXISTS documents (
            collection TEXT NOT NULL,
            id TEXT NOT NULL,
            version INTEGER NOT NULL,
            body TEXT NOT NULL,
            PRIMARY KEY (collection, id)
        ) WITHOUT ROWID;
        """,
        // Version 2: explicit collections, tombstones (body NULL) and a per-database commit offset.
        // A document's version becomes the offset of its last write; offsets continue after every
        // version 1 counter so no document ever sees a version again.
        """
        CREATE TABLE collections (name TEXT PRIMARY KEY, created_at INTEGER NOT NULL) WITHOUT ROWID;
        CREATE TABLE commit_offset (id INTEGER PRIMARY KEY CHECK (id = 1), value INTEGER NOT NULL);
        CREATE TABLE documents_v2 (
            collection TEXT NOT NULL,
            id TEXT NOT NULL,
            version INTEGER NOT NULL,
            body TEXT,
            PRIMARY KEY (collection, id)
        ) WITHOUT ROWID;
        INSERT INTO documents_v2 (collection, id, version, body) SELECT collection, id, version, body FROM documents;
        DROP TABLE documents;
        ALTER TABLE documents_v2 RENAME TO documents;
        INSERT INTO collections (name, created_at) SELECT DISTINCT collection, CAST(unixepoch('subsec') * 1000 AS INTEGER) FROM documents;
        INSERT INTO commit_offset (id, value) SELECT 1, coalesce(max(version), 0) FROM documents;
        """,
        // Version 3 also upgrades databases opened by an earlier build of the document API.
        "CREATE INDEX documents_live ON documents (collection, id) WHERE body IS NOT NULL;",
        // Version 4: offsets can restart after database recreation; versions also need a persisted incarnation.
        """
        ALTER TABLE commit_offset ADD COLUMN incarnation TEXT NOT NULL DEFAULT '';
        """,
        // Version 5: shape history. Every write is retained in change_log so shape snapshots at an
        // offset and closed log chunks never change (docs/shape-reads.md). Collections get a random
        // generation per creation. Existing files start history at their head, with their current
        // rows as the baseline: older offsets are below the floor and answer 409 must_refetch.
        """
        ALTER TABLE collections ADD COLUMN generation INTEGER NOT NULL DEFAULT 0;
        UPDATE collections SET generation = random();
        CREATE TABLE shape_history (id INTEGER PRIMARY KEY CHECK (id = 1), floor INTEGER NOT NULL);
        INSERT INTO shape_history (id, floor) SELECT 1, value FROM commit_offset;
        CREATE TABLE change_log (
            sequence INTEGER PRIMARY KEY,
            collection TEXT NOT NULL,
            generation INTEGER NOT NULL,
            id TEXT NOT NULL,
            version INTEGER NOT NULL,
            body TEXT
        );
        CREATE INDEX change_log_by_offset ON change_log (collection, generation, version, sequence);
        CREATE INDEX change_log_by_document ON change_log (collection, generation, id, version, sequence);
        INSERT INTO collections (name, created_at, generation)
            SELECT collection, CAST(unixepoch('subsec') * 1000 AS INTEGER), random() FROM documents
            WHERE collection NOT IN (SELECT name FROM collections) GROUP BY collection;
        """,
    ];

    // Version 5's history baseline, for client databases only: _system keeps no shape history, so its
    // existing records (invitations, audit events) must not be copied where its deletes never reach.
    private const string ShapeBaseline = """
        INSERT INTO change_log (collection, generation, id, version, body)
            SELECT d.collection, c.generation, d.id, d.version, d.body FROM documents d JOIN collections c ON c.name = d.collection
            ORDER BY d.version, d.collection, d.id;
        """;

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

    // Signals every write commit, collection-only ones included, to WaitForCommitAsync.
    private readonly Lock _commits = new();
    private long _sequence;
    private TaskCompletionSource _signal = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Whether this is TerraBase's own database, which keeps no shape history.</summary>
    internal bool IsSystem => name == SqliteStorageEngine.SystemName;

    public long CommitSequence
    {
        get
        {
            lock (_commits)
            {
                return _sequence;
            }
        }
    }

    public Task WaitForCommitAsync(long sequence, CancellationToken cancellationToken)
    {
        Task signal;
        lock (_commits)
        {
            if (_sequence > sequence)
            {
                return Task.CompletedTask;
            }

            signal = _signal.Task;
        }

        return signal.WaitAsync(cancellationToken);
    }

    public Guid Incarnation { get; private set; }

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
            return new SqliteStorageTransaction(this, session, session.Connection.BeginTransaction(deferred: !write), write, write ? Committed : null);
        }
        catch
        {
            pool.Discard(session);
            throw;
        }
    }

    private void Committed()
    {
        TaskCompletionSource signal;
        lock (_commits)
        {
            _sequence++;
            signal = _signal;
            _signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        signal.SetResult();
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
            pool.Discard(session);
            throw;
        }

        Return(session);
        // InitializeAsync switches the copy to WAL and refuses an unsupported schema version.
        await InitializeAsync(name, path, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Takes back a session whose work has ended; the pool keeps it idle or closes it.</summary>
    internal void Return(SqliteSession session) => pool.Return(this, session);

    /// <summary>Closes a session whose transaction failed instead of pooling it.</summary>
    internal void Discard(SqliteSession session) => pool.Discard(session);

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

        SqliteSession session;
        try
        {
            session = await SqliteSession.OpenAsync(_connectionString, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            pool.OpenFailed();
            throw;
        }

        return pool.Adopt(this, session);
    }

    /// <summary>
    /// Creates (or adopts a copied) and migrates a new database file at <paramref name="path"/> on an unpooled
    /// connection, so no handle stays open once this returns and the file can be moved.
    /// </summary>
    internal static async Task InitializeAsync(string name, string path, CancellationToken cancellationToken)
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
            // Initialize publishes a new database, including a VACUUM copy: give it its own identity.
            await RenewIncarnationAsync(connection, null, cancellationToken).ConfigureAwait(false);
        }
    }

    internal async Task MigrateAsync(CancellationToken cancellationToken)
    {
        var session = await RentAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await MigrateAsync(name, session.Connection, cancellationToken).ConfigureAwait(false);
            Incarnation = Guid.ParseExact(
                (string)(await ScalarAsync(session.Connection, null, SqliteStatements.Incarnation, cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("database incarnation is missing")), "N");
        }
        catch
        {
            pool.Discard(session);
            throw;
        }

        Return(session);
    }

    private static async Task MigrateAsync(string name, SqliteConnection connection, CancellationToken cancellationToken)
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
            var version = await VersionAsync(name, connection, transaction, cancellationToken).ConfigureAwait(false);
            for (; version < SchemaVersion; version++)
            {
                await ScalarAsync(
                    connection, transaction, Migrations[version] + $"PRAGMA user_version = {version + 1};", cancellationToken).ConfigureAwait(false);
                if (version == 3)
                {
                    await RenewIncarnationAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
                }

                if (version == 4 && name != SqliteStorageEngine.SystemName)
                {
                    await ScalarAsync(connection, transaction, ShapeBaseline, cancellationToken).ConfigureAwait(false);
                }
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    // True when the schema is current; throws for a newer, unsupported schema.
    private static async Task<bool> IsCurrentAsync(
        string name, SqliteConnection connection, SqliteTransaction? transaction, CancellationToken cancellationToken) =>
        await VersionAsync(name, connection, transaction, cancellationToken).ConfigureAwait(false) == SchemaVersion;

    private static async Task<int> VersionAsync(
        string name, SqliteConnection connection, SqliteTransaction? transaction, CancellationToken cancellationToken)
    {
        var version = Convert.ToInt32(
            await ScalarAsync(connection, transaction, "PRAGMA user_version;", cancellationToken).ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture);
        return version > SchemaVersion
            ? throw new InvalidOperationException($"database '{name}' has schema version {version}; this build supports up to {SchemaVersion}")
            : version;
    }

    private static async Task RenewIncarnationAsync(
        SqliteConnection connection, SqliteTransaction? transaction, CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        await using (command.ConfigureAwait(false))
        {
            command.Transaction = transaction;
            command.CommandText = SqliteStatements.RenewIncarnation;
            command.Parameters.AddWithValue("$incarnation", Guid.NewGuid().ToString("N"));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
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

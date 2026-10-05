using Microsoft.Data.Sqlite;
using TerraScale.TerraBase.Tenancy;

namespace TerraScale.TerraBase.Engine.Sqlite;

/// <param name="name">Names the database in error messages: its id, or <c>_system</c>.</param>
/// <param name="path">The database file.</param>
/// <param name="pool">Idle sessions, shared by every database of the engine.</param>
/// <param name="tenant">The storage identity; null for the system database.</param>
/// <param name="time">The clock of the query deadline.</param>
/// <param name="changesCommitted">Receives the last captured sequence after a write commits.</param>
/// <param name="committed">Optional engine notification after a successful commit that changed this database.</param>
internal sealed class SqliteDatabaseStore(
    string name, string path, SqliteSessionPool pool, TenantContext? tenant, TimeProvider time,
    Action<long>? changesCommitted, Action? committed) : IDatabaseStore, IDisposable
{
    internal SqliteDatabaseStore(string name, string path, SqliteSessionPool pool, TimeProvider time, Action? committed = null)
        : this(name, path, pool, null, time, null, committed) { }

    internal SqliteDatabaseStore(string name, string path, SqliteSessionPool pool, TenantContext? tenant = null, Action<long>? changesCommitted = null)
        : this(name, path, pool, tenant, TimeProvider.System, changesCommitted, null) { }
    /// <summary>On-disk schema version, stored in <c>PRAGMA user_version</c>.</summary>
    private const int SchemaVersion = 8;

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
        // Version 6: each document's first write per collection generation, so a snapshot finds the ids that
        // existed at its offset without reading the revisions of later ones, and the generation's highest id
        // over time (a new row each time a new id sorts above every earlier one), so a snapshot stops at the
        // highest id at its offset and never walks ids appended later, such as UUIDv7s. Existing history is
        // backfilled with each id's earliest retained version; the trigger keeps every later write. Offsets
        // grow in commit order, so the highest id at an offset is that of its latest row at or before it.
        """
        CREATE TABLE change_log_births (
            collection TEXT NOT NULL,
            generation INTEGER NOT NULL,
            id TEXT NOT NULL,
            version INTEGER NOT NULL,
            PRIMARY KEY (collection, generation, id, version)
        ) WITHOUT ROWID;
        INSERT INTO change_log_births (collection, generation, id, version)
            SELECT collection, generation, id, min(version) FROM change_log GROUP BY collection, generation, id;
        CREATE TABLE change_log_high (
            collection TEXT NOT NULL,
            generation INTEGER NOT NULL,
            version INTEGER NOT NULL,
            id TEXT NOT NULL,
            PRIMARY KEY (collection, generation, version, id)
        ) WITHOUT ROWID;
        INSERT INTO change_log_high (collection, generation, version, id)
            SELECT collection, generation, version, id FROM (
                SELECT collection, generation, version, id,
                    max(id) OVER (PARTITION BY collection, generation ORDER BY version, id ROWS UNBOUNDED PRECEDING) AS high
                FROM change_log_births)
            WHERE id = high;
        CREATE TRIGGER change_log_birth AFTER INSERT ON change_log
        WHEN NOT EXISTS (
            SELECT 1 FROM change_log_births b WHERE b.collection = NEW.collection AND b.generation = NEW.generation AND b.id = NEW.id)
        BEGIN
            INSERT INTO change_log_births (collection, generation, id, version) VALUES (NEW.collection, NEW.generation, NEW.id, NEW.version);
            INSERT INTO change_log_high (collection, generation, version, id)
                SELECT NEW.collection, NEW.generation, NEW.version, NEW.id
                WHERE NEW.id > coalesce((
                    SELECT h.id FROM change_log_high h WHERE h.collection = NEW.collection AND h.generation = NEW.generation
                    ORDER BY h.version DESC, h.id DESC LIMIT 1), '');
        END;
        """,
        // Version 7: capture and applied frontiers use the existing incarnation in commit_offset.
        """
        ALTER TABLE change_log ADD COLUMN document_version INTEGER NOT NULL DEFAULT 0;
        UPDATE change_log SET document_version = version;
        CREATE TABLE changes (
            sequence INTEGER NOT NULL,
            ordinal INTEGER NOT NULL,
            collection TEXT NOT NULL,
            id TEXT NOT NULL,
            version INTEGER NOT NULL,
            body TEXT,
            PRIMARY KEY (sequence, ordinal)
        ) WITHOUT ROWID;
        CREATE INDEX changes_deleted ON changes (collection, id, version) WHERE body IS NULL;
        CREATE TABLE replication_state (id INTEGER PRIMARY KEY CHECK (id = 1), complete INTEGER NOT NULL);
        INSERT INTO replication_state (id, complete) SELECT 1, NOT EXISTS (SELECT 1 FROM documents);
        CREATE TABLE applied (origin BLOB PRIMARY KEY, sequence INTEGER NOT NULL) WITHOUT ROWID;
        """,
        // Version 8: a sequence alone cannot identify a prefix after snapshot restoration and replacement writes.
        // Legacy applied prefixes have no content proof: replay them rather than trusting an ambiguous acknowledgment.
        """
        CREATE TABLE journal_prefixes (
            sequence INTEGER PRIMARY KEY CHECK (sequence > 0),
            digest BLOB NOT NULL CHECK (length(digest) = 32)
        ) WITHOUT ROWID;
        ALTER TABLE applied ADD COLUMN digest BLOB CHECK (digest IS NULL OR length(digest) = 32);
        DELETE FROM applied;
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

    public TenantContext? Tenant { get; } = tenant;

    public Guid Incarnation { get; private set; }

    /// <summary>Set once the database is deleted or the engine shuts down; guarded by <see cref="SqliteSessionPool"/>.</summary>
    internal bool Closed
    {
        get => Volatile.Read(ref field);
        set => Volatile.Write(ref field, value);
    }

    public bool IsClosed => Closed;

    internal TimeProvider Time => time;

    /// <summary>Idle sessions of this database, for tests.</summary>
    internal int IdleCount => pool.IdleCountOf(this);

    public async ValueTask<IStorageTransaction> BeginTransactionAsync(bool write, CancellationToken cancellationToken)
    {
        var session = await RentAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var transaction = await session.ExecuteAsync(async () =>
            {
                // BEGIN DEFERRED alone still permits a write upgrade. SQLite enforces read-only
                // access before BEGIN; both setup and native lock acquisition share the caller's budget.
                if (!write)
                {
                    var readOnly = session.Connection.CreateCommand();
                    await using (readOnly.ConfigureAwait(false))
                    {
                        readOnly.CommandText = "PRAGMA query_only = ON;";
                        await readOnly.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                    }
                }

                // Writers take the lock up front so they wait instead of failing on upgrade.
                return session.Connection.BeginTransaction(deferred: !write);
            }, cancellationToken).ConfigureAwait(false);
            return new SqliteStorageTransaction(this, session, transaction, write, write ? Committed : null, write ? changesCommitted : null);
        }
        catch
        {
            pool.Discard(session);
            throw;
        }
    }

    private void Committed()
    {
        committed?.Invoke();
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
        await InitializeAsync(name, path, SqliteStatements.MaterializeRetainedDeletions + """
            UPDATE replication_state SET complete = 0 WHERE id = 1;
            DELETE FROM changes;
            DELETE FROM journal_prefixes;
            DELETE FROM applied;
            """, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// When the change log does not cover this database's state (a copy, or an upgrade from version 1 with
    /// data), appends the current state of every document and tombstone as baseline change groups, one
    /// bounded group per transaction, then marks the log complete. Merging is idempotent, so a baseline
    /// repeated after a crash only costs bytes. Returns the last appended sequence (0 when none) and the
    /// pre-existing states skipped for failing replication admission.
    /// </summary>
    internal async Task<(long Sequence, int Skipped)> SeedAsync(CancellationToken cancellationToken)
    {
        var (sequence, skipped) = (0L, 0);
        foreach (var tombstones in new[] { false, true })
        {
            (string Collection, string Id)? after = null;
            while (true)
            {
                var transaction = (SqliteStorageTransaction)await BeginTransactionAsync(write: true, cancellationToken).ConfigureAwait(false);
                await using (transaction.ConfigureAwait(false))
                {
                    if (sequence == 0 && after is null && !tombstones)
                    {
                        if (await transaction.IsCompleteAsync(cancellationToken).ConfigureAwait(false))
                        {
                            return (0, 0);
                        }

                        // SQL skips oversized keys without bringing them into managed memory or the resume cursor.
                        skipped = await transaction.CountOversizedBaselineKeysAsync(cancellationToken).ConfigureAwait(false);
                    }

                    if (await transaction.AppendBaselineAsync(tombstones, after, cancellationToken).ConfigureAwait(false) is not { } chunk)
                    {
                        break;
                    }

                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                    after = chunk.Last;
                    sequence = Math.Max(sequence, chunk.Sequence);
                    skipped += chunk.Skipped;
                }
            }
        }

        var complete = (SqliteStorageTransaction)await BeginTransactionAsync(write: true, cancellationToken).ConfigureAwait(false);
        await using (complete.ConfigureAwait(false))
        {
            await complete.MarkCompleteAsync(cancellationToken).ConfigureAwait(false);
            await complete.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        return (sequence, skipped);
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
        catch (Exception error)
        {
            pool.OpenFailed();
            if (Closed && !cancellationToken.IsCancellationRequested)
            {
                // A delete can unlink the file after Rent found no session but before SQLite opened it.
                throw new ObjectDisposedException(GetType().FullName, error);
            }

            throw;
        }

        return pool.Adopt(this, session);
    }

    /// <summary>
    /// Creates (or adopts a copied) and migrates a new database file at <paramref name="path"/> on an unpooled
    /// connection, so no handle stays open once this returns and the file can be moved.
    /// </summary>
    internal static async Task InitializeAsync(string name, string path, string? afterMigration, CancellationToken cancellationToken)
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
            // Publish a fresh identity and prepare the copied baseline atomically, on this file only.
            var transaction = connection.BeginTransaction(deferred: false);
            await using (transaction.ConfigureAwait(false))
            {
                await RenewIncarnationAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
                if (afterMigration is not null)
                {
                    await ScalarAsync(connection, transaction, afterMigration, cancellationToken).ConfigureAwait(false);
                }

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    internal async Task MigrateAsync(CancellationToken cancellationToken)
    {
        var session = await RentAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await session.ExecuteAsync(async () =>
            {
                // A migration is atomic and rolls back before a lock error leaves this scope.
                await MigrateAsync(name, session.Connection, cancellationToken).ConfigureAwait(false);
                Incarnation = Guid.ParseExact(
                    (string)(await ScalarAsync(session.Connection, null, SqliteStatements.Incarnation, cancellationToken).ConfigureAwait(false)
                        ?? throw new InvalidOperationException("database incarnation is missing")), "N");
                return 0;
            }, cancellationToken).ConfigureAwait(false);
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

                if (version == 7)
                {
                    await BackfillJournalDigestsAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
                }
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    // The migration retains at most one 1024-item/2-MiB group, never the whole journal. The prefix chain
    // and user_version publish together, so interruption cannot leave a partially trusted journal.
    private static async Task BackfillJournalDigestsAsync(
        SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        var origin = (byte[])(await ScalarAsync(connection, transaction, SqliteStatements.Origin, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("the journal origin is missing"));
        var previous = JournalDigest.Initial(origin);
        var head = (long)(await ScalarAsync(connection, transaction, SqliteStatements.LastSequence, cancellationToken).ConfigureAwait(false))!;
        var read = connection.CreateCommand();
        var append = connection.CreateCommand();
        await using (read.ConfigureAwait(false))
        await using (append.ConfigureAwait(false))
        {
            read.Transaction = append.Transaction = transaction;
            read.CommandText = SqliteStatements.ReadChanges;
            read.Parameters.AddWithValue("$sequence", 0L);
            read.Parameters.AddWithValue("$limit", IStorageTransaction.MaxChangeGroupChanges + 1);
            read.Parameters.AddWithValue("$maxBytes", IStorageTransaction.MaxChangeGroupBytes);
            append.CommandText = SqliteStatements.AppendChangeDigest;
            append.Parameters.AddWithValue("$sequence", 0L);
            append.Parameters.AddWithValue("$digest", previous);
            for (var sequence = 1L; sequence <= head;)
            {
                read.Parameters["$sequence"].Value = sequence;
                var changes = await SqliteStorageTransaction.ReadChangesAsync(read, cancellationToken).ConfigureAwait(false);
                if (changes.Count == 0)
                {
                    throw new InvalidDataException("the captured journal is not contiguous");
                }

                previous = JournalDigest.Compute(origin, sequence, previous, changes);
                append.Parameters["$sequence"].Value = sequence;
                append.Parameters["$digest"].Value = previous;
                await append.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                if (sequence == head)
                {
                    break;
                }

                sequence++;
            }
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

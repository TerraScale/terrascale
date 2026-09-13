using Microsoft.Data.Sqlite;
using TerraBase.Storage.Migrations;

namespace TerraBase.Storage.Schema;

/// <summary>
/// Owns the SQLite connection contract and the durable schema used by one
/// TerraBase database file.
/// </summary>
/// <remarks>
/// SQLite settings are connection scoped. Every writer and reader therefore
/// goes through <see cref="ConfigureConnection"/> before it is used. The
/// schema is deliberately kept in SQL here instead of being inferred from
/// CLR models: a database can be opened by a newer process and the migration
/// runner can verify exactly what was applied.
/// </remarks>
public static class SqliteSchema
{
    /// <summary>The latest schema migration version.</summary>
    public const int CurrentVersion = 4;

    /// <summary>The busy timeout applied to every SQLite connection.</summary>
    public const int BusyTimeoutMilliseconds = 60_000;

    /// <summary>The maximum WAL size SQLite should retain between checkpoints.</summary>
    public const long JournalSizeLimitBytes = 64 * 1024 * 1024;

    /// <summary>Returns the complete, ordered schema migration catalog.</summary>
    public static IReadOnlyList<SqliteMigration> Migrations => migrationCatalog.Value;

    private static readonly Lazy<IReadOnlyList<SqliteMigration>> migrationCatalog = new(() =>
    [
        new SqliteMigration(1, "storage-core", StorageCoreSql),
        new SqliteMigration(2, "schema-and-auto-index-catalog", SchemaAndAutoIndexSql),
        new SqliteMigration(3, "document-publication-compatibility", DocumentPublicationCompatibilitySql),
        new SqliteMigration(4, "document-publication-backfill", DocumentPublicationBackfillSql),
    ]);

    /// <summary>
    /// Configures one opened connection for TerraBase durability and
    /// concurrency semantics.
    /// </summary>
    /// <param name="connection">The connection to configure.</param>
    /// <exception cref="ArgumentNullException">The connection is null.</exception>
    /// <exception cref="InvalidOperationException">
    /// The file could not be placed in WAL mode.
    /// </exception>
    public static void ConfigureConnection(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (connection.State != System.Data.ConnectionState.Open)
        {
            connection.Open();
        }

        ExecutePragma(connection, "PRAGMA foreign_keys = ON;");
        ExecutePragma(connection, $"PRAGMA busy_timeout = {BusyTimeoutMilliseconds};");
        ExecutePragma(connection, $"PRAGMA journal_size_limit = {JournalSizeLimitBytes};");
        ExecutePragma(connection, "PRAGMA synchronous = FULL;");

        var journalMode = ExecuteScalar(connection, "PRAGMA journal_mode = WAL;")?.ToString();
        // SQLite's in-memory provider cannot use WAL. Keeping memory mode
        // usable makes isolated unit tests possible while every file-backed
        // database still fails closed if WAL was not accepted.
        if (!string.Equals(journalMode, "wal", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(journalMode, "memory", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"TerraBase requires SQLite WAL mode, but the connection reported '{journalMode ?? "<null>"}'.");
        }
    }

    /// <summary>Applies connection settings and all pending schema migrations.</summary>
    /// <param name="connection">An SQLite connection.</param>
    /// <param name="cancellationToken">The cancellation signal.</param>
    public static async Task EnsureCreatedAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ConfigureConnection(connection);
        await SqliteMigrationRunner.ApplyAsync(connection, Migrations, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Applies connection settings and all pending schema migrations.</summary>
    /// <param name="connection">An SQLite connection.</param>
    public static void EnsureCreated(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ConfigureConnection(connection);
        SqliteMigrationRunner.Apply(connection, Migrations);
    }

    private static readonly string StorageCoreSql = """
        CREATE TABLE IF NOT EXISTS meta (
            key   TEXT PRIMARY KEY,
            value BLOB NOT NULL
        ) WITHOUT ROWID;

        CREATE TABLE IF NOT EXISTS documents (
            tenant     TEXT    NOT NULL,
            collection TEXT    NOT NULL,
            id         TEXT    NOT NULL,
            commit_seq INTEGER NOT NULL CHECK (commit_seq >= 0),
            version    INTEGER NOT NULL CHECK (version > 0),
            deleted    INTEGER NOT NULL CHECK (deleted IN (0, 1)),
            body       BLOB    NOT NULL,
            updated_at TEXT    NOT NULL,
            PRIMARY KEY (tenant, collection, id, commit_seq)
        ) WITHOUT ROWID;

        CREATE INDEX IF NOT EXISTS documents_tenant_id_seq
            ON documents (tenant, id, commit_seq);
        CREATE INDEX IF NOT EXISTS documents_collection_seq
            ON documents (tenant, collection, commit_seq);

        CREATE TABLE IF NOT EXISTS ledger (
            tenant     TEXT    NOT NULL,
            operation  TEXT    NOT NULL,
            digest     INTEGER NOT NULL,
            sequence   INTEGER NOT NULL CHECK (sequence >= 0),
            root       TEXT    NOT NULL,
            requested  INTEGER NOT NULL,
            committed  INTEGER NOT NULL CHECK (committed IN (0, 1)),
            synced     INTEGER NOT NULL CHECK (synced IN (0, 1)),
            rejection  INTEGER NOT NULL,
            PRIMARY KEY (tenant, operation)
        ) WITHOUT ROWID;

        CREATE INDEX IF NOT EXISTS ledger_sequence ON ledger (sequence);

        CREATE TABLE IF NOT EXISTS journal (
            sequence  INTEGER PRIMARY KEY CHECK (sequence >= 0),
            tenant    TEXT NOT NULL,
            operation TEXT NOT NULL,
            frame     BLOB NOT NULL
        );

        CREATE INDEX IF NOT EXISTS journal_tenant_sequence
            ON journal (tenant, sequence);

        CREATE TABLE IF NOT EXISTS usage_facts (
            tenant       TEXT    NOT NULL,
            project      TEXT    NOT NULL,
            database_name TEXT   NOT NULL,
            partition    TEXT    NOT NULL,
            region       TEXT    NOT NULL,
            rule         INTEGER NOT NULL,
            fact_id      TEXT    NOT NULL,
            seq          INTEGER NOT NULL CHECK (seq >= 0),
            start_millis INTEGER NOT NULL,
            end_millis   INTEGER NOT NULL,
            body         BLOB    NOT NULL,
            PRIMARY KEY (tenant, project, database_name, partition, region, rule, fact_id)
        ) WITHOUT ROWID;

        CREATE INDEX IF NOT EXISTS usage_facts_tenant_seq
            ON usage_facts (tenant, seq);
        CREATE INDEX IF NOT EXISTS usage_facts_seq ON usage_facts (seq);

        CREATE TABLE IF NOT EXISTS consumer_frontiers (
            name TEXT PRIMARY KEY,
            seq  INTEGER NOT NULL CHECK (seq >= 0)
        ) WITHOUT ROWID;
        """;

    private static readonly string SchemaAndAutoIndexSql = """
        CREATE TABLE IF NOT EXISTS schema_definitions (
            tenant       TEXT    NOT NULL,
            collection   TEXT    NOT NULL,
            version      INTEGER NOT NULL CHECK (version > 0),
            definition   BLOB    NOT NULL CHECK (length(definition) > 0),
            digest       BLOB    NOT NULL CHECK (length(digest) > 0),
            active       INTEGER NOT NULL CHECK (active IN (0, 1)),
            declared_at TEXT    NOT NULL,
            PRIMARY KEY (tenant, collection, version)
        ) WITHOUT ROWID;

        CREATE UNIQUE INDEX IF NOT EXISTS schema_definitions_active
            ON schema_definitions (tenant, collection)
            WHERE active = 1;

        CREATE TABLE IF NOT EXISTS migration_records (
            tenant           TEXT    NOT NULL,
            collection       TEXT    NOT NULL,
            migration_id     TEXT    NOT NULL,
            from_version     INTEGER NOT NULL CHECK (from_version > 0),
            to_version       INTEGER NOT NULL CHECK (to_version > from_version),
            state            TEXT    NOT NULL CHECK (state IN ('planned', 'running', 'rolling_back', 'completed', 'failed', 'rolled_back')),
            cursor           INTEGER NOT NULL DEFAULT 0 CHECK (cursor >= 0),
            documents        INTEGER NOT NULL DEFAULT 0 CHECK (documents >= 0),
            transformed      INTEGER NOT NULL DEFAULT 0 CHECK (transformed >= 0),
            pages            INTEGER NOT NULL DEFAULT 0 CHECK (pages >= 0),
            contract         BLOB    NOT NULL CHECK (length(contract) > 0),
            source_digest    BLOB,
            result_digest    BLOB,
            error_code       TEXT,
            updated_at       TEXT    NOT NULL,
            PRIMARY KEY (tenant, collection, migration_id)
        ) WITHOUT ROWID;

        CREATE UNIQUE INDEX IF NOT EXISTS migration_records_active
            ON migration_records (tenant, collection)
            WHERE state IN ('planned', 'running', 'rolling_back');

        CREATE TABLE IF NOT EXISTS index_descriptors (
            descriptor_id      TEXT    PRIMARY KEY,
            tenant             TEXT    NOT NULL,
            collection         TEXT    NOT NULL,
            path               TEXT    NOT NULL,
            value_type         TEXT    NOT NULL CHECK (value_type IN ('boolean', 'integer', 'number', 'text', 'bytes')),
            state               TEXT    NOT NULL CHECK (state IN ('candidate', 'building', 'active', 'invisible', 'retired', 'blocked')),
            planner_visible    INTEGER NOT NULL DEFAULT 0 CHECK (planner_visible IN (0, 1)),
            backfill_watermark INTEGER NOT NULL DEFAULT 0 CHECK (backfill_watermark >= 0),
            created_at         TEXT    NOT NULL,
            updated_at         TEXT    NOT NULL,
            UNIQUE (tenant, collection, path, value_type)
        );

        CREATE INDEX IF NOT EXISTS index_descriptors_collection_state
            ON index_descriptors (tenant, collection, state);

        CREATE TABLE IF NOT EXISTS index_catalog (
            index_id       TEXT    PRIMARY KEY,
            descriptor_id  TEXT    NOT NULL REFERENCES index_descriptors (descriptor_id),
            tenant         TEXT    NOT NULL,
            collection     TEXT    NOT NULL,
            physical_name  TEXT    NOT NULL UNIQUE,
            columns        BLOB    NOT NULL CHECK (length(columns) > 0),
            state          TEXT    NOT NULL CHECK (state IN ('candidate', 'building', 'active', 'invisible', 'retired', 'blocked')),
            planner_visible INTEGER NOT NULL DEFAULT 0 CHECK (planner_visible IN (0, 1)),
            built_watermark INTEGER NOT NULL DEFAULT 0 CHECK (built_watermark >= 0),
            created_at     TEXT    NOT NULL,
            updated_at     TEXT    NOT NULL,
            FOREIGN KEY (descriptor_id) REFERENCES index_descriptors (descriptor_id)
        );

        CREATE INDEX IF NOT EXISTS index_catalog_collection_state
            ON index_catalog (tenant, collection, state);

        CREATE TABLE IF NOT EXISTS index_usage (
            tenant          TEXT    NOT NULL,
            collection      TEXT    NOT NULL,
            index_id        TEXT    NOT NULL REFERENCES index_catalog (index_id),
            template        TEXT    NOT NULL,
            executions      INTEGER NOT NULL DEFAULT 0 CHECK (executions >= 0),
            logical_cost    INTEGER NOT NULL DEFAULT 0 CHECK (logical_cost >= 0),
            rows_returned  INTEGER NOT NULL DEFAULT 0 CHECK (rows_returned >= 0),
            last_used_at    TEXT,
            PRIMARY KEY (tenant, collection, index_id, template)
        ) WITHOUT ROWID;

        CREATE TABLE IF NOT EXISTS index_history (
            event_id      INTEGER PRIMARY KEY AUTOINCREMENT,
            tenant        TEXT NOT NULL,
            collection    TEXT NOT NULL,
            index_id      TEXT,
            descriptor_id TEXT,
            previous_state TEXT,
            next_state    TEXT NOT NULL,
            evidence      BLOB NOT NULL,
            recorded_at   TEXT NOT NULL
        );
        """;

    private static readonly string DocumentPublicationCompatibilitySql = """
        ALTER TABLE documents ADD COLUMN document_id TEXT NOT NULL DEFAULT '';
        ALTER TABLE documents ADD COLUMN commit_ulid TEXT NOT NULL DEFAULT '';
        ALTER TABLE documents ADD COLUMN revision INTEGER NOT NULL DEFAULT 1 CHECK (revision > 0);
        ALTER TABLE documents ADD COLUMN context BLOB NOT NULL DEFAULT X'';
        ALTER TABLE documents ADD COLUMN updated_at_ms INTEGER NOT NULL DEFAULT 0;
        UPDATE documents SET document_id = id WHERE document_id = '';

        CREATE TABLE IF NOT EXISTS version_watermarks (
            tenant       TEXT NOT NULL,
            collection   TEXT NOT NULL,
            document_id  TEXT NOT NULL,
            version      INTEGER NOT NULL CHECK (version > 0),
            PRIMARY KEY (tenant, collection, document_id)
        ) WITHOUT ROWID;

        CREATE TABLE IF NOT EXISTS publication_journal (
            sequence     INTEGER PRIMARY KEY AUTOINCREMENT,
            operation    TEXT NOT NULL,
            tenant       TEXT NOT NULL,
            collection   TEXT NOT NULL,
            document_id  TEXT NOT NULL,
            version      INTEGER NOT NULL CHECK (version > 0),
            body         BLOB,
            updated_at_ms INTEGER NOT NULL
        );

        CREATE INDEX IF NOT EXISTS publication_journal_key
            ON publication_journal (tenant, collection, document_id, sequence);

        CREATE TABLE IF NOT EXISTS operation_facts (
            tenant     TEXT NOT NULL,
            operation  TEXT NOT NULL,
            fact       BLOB NOT NULL,
            PRIMARY KEY (tenant, operation)
        ) WITHOUT ROWID;

        CREATE TABLE IF NOT EXISTS family_metadata (
            tenant    TEXT NOT NULL,
            family    TEXT NOT NULL,
            metadata  BLOB NOT NULL,
            PRIMARY KEY (tenant, family)
        ) WITHOUT ROWID;

        CREATE TABLE IF NOT EXISTS schema_metadata (
            key       TEXT PRIMARY KEY,
            value     TEXT NOT NULL
        ) WITHOUT ROWID;

        INSERT INTO schema_metadata (key, value)
        VALUES ('schema_version', '3')
        ON CONFLICT (key) DO UPDATE SET value = excluded.value;
        """;

    private static readonly string DocumentPublicationBackfillSql = """
        UPDATE documents
        SET revision = CASE WHEN revision < version THEN version ELSE revision END,
            updated_at_ms = CASE
                WHEN updated_at_ms <> 0 THEN updated_at_ms
                ELSE COALESCE(CAST(strftime('%s', updated_at) AS INTEGER) * 1000, 0)
            END;

        INSERT INTO version_watermarks (tenant, collection, document_id, version)
        SELECT tenant, collection, document_id, MAX(version)
        FROM documents
        GROUP BY tenant, collection, document_id
        ON CONFLICT (tenant, collection, document_id)
        DO UPDATE SET version = CASE
            WHEN version_watermarks.version < excluded.version THEN excluded.version
            ELSE version_watermarks.version
        END;
        """;

    private static void ExecutePragma(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static object? ExecuteScalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }
}

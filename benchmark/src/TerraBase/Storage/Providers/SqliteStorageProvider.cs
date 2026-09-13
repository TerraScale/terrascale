using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace TerraBase;

/// <summary>
/// A SQLite-backed document provider for a single TerraBase database file.
/// </summary>
/// <remarks>
/// SQLite is the durable source of truth for both the current document and
/// its version watermark. Every mutation updates the document (or its
/// watermark) and the publication journal in one transaction. The journal is
/// intentionally private until the public storage contract grows a cursor
/// surface; keeping it here makes the commit boundary durable today without
/// inventing an incompatible API.
///
/// Bodies are stored as UTF-8 JSON bytes. Reads parse a body only to produce
/// the existing <see cref="Document"/> value; SQLite never interprets body
/// contents or executes JSON expressions against them.
/// </remarks>
public sealed class SqliteStorageProvider : IStorageProvider
{
    private const int SchemaVersion = 1;
    private const long MaximumSqliteInteger = long.MaxValue;
    private const string JournalMode = "WAL";
    private static readonly ConcurrentDictionary<string, object> PathGates =
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private readonly object gate = new();
    private readonly object pathGate;
    private readonly SqliteConnection connection;
    private readonly string storagePath;
    private bool disposed;

    /// <summary>Opens or creates one SQLite database file.</summary>
    /// <param name="path">The database file path.</param>
    public SqliteStorageProvider(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        storagePath = Path.GetFullPath(path);
        pathGate = PathGates.GetOrAdd(storagePath, static _ => new object());
        var parent = Path.GetDirectoryName(storagePath);
        if (parent is null)
        {
            throw new ArgumentException("The database path must have a parent directory.", nameof(path));
        }

        if (Directory.Exists(storagePath))
        {
            throw new IOException($"The SQLite storage path is a directory: {storagePath}");
        }

        Directory.CreateDirectory(parent);
        connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = storagePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            ForeignKeys = true,
            DefaultTimeout = 60,
        }.ToString());

        lock (pathGate)
        {
            connection.Open();
            ConfigureConnection();
            EnsureSchema();
        }
    }

    /// <summary>The fully qualified SQLite database path.</summary>
    public string StoragePath => storagePath;

    /// <inheritdoc />
    public ValueTask<Document?> ReadAsync(DocumentKey key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            ThrowIfDisposed();
            lock (pathGate)
            {
                using var command = connection.CreateCommand();
                command.CommandText = """
                    SELECT body, version, updated_at_ms
                    FROM documents
                    WHERE tenant = $tenant AND collection = $collection AND document_id = $document_id;
                    """;
                BindKey(command, key);
                using var reader = command.ExecuteReader();
                return ValueTask.FromResult(reader.Read() ? ReadDocument(key, reader) : null);
            }
        }
    }

    /// <inheritdoc />
    public ValueTask<StorageWriteResult> UpsertAsync(
        DocumentKey key,
        JsonElement body,
        DocumentPrecondition precondition,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var bodyBytes = EncodeBody(body);

        lock (gate)
        {
            ThrowIfDisposed();
            lock (pathGate)
            {
                using var transaction = connection.BeginTransaction();
                var current = ReadCurrent(key, transaction);
                if (!precondition.Matches(current))
                {
                    transaction.Rollback();
                    return ValueTask.FromResult(StorageWriteResult.Conflict);
                }

                var previousVersion = ReadWatermark(key, transaction);
                var version = NextVersion(previousVersion);
                var updatedAt = DateTimeOffset.UtcNow;
                using (var command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText = """
                        INSERT INTO documents (tenant, collection, document_id, body, version, updated_at_ms)
                        VALUES ($tenant, $collection, $document_id, $body, $version, $updated_at_ms)
                        ON CONFLICT (tenant, collection, document_id) DO UPDATE SET
                            body = excluded.body,
                            version = excluded.version,
                            updated_at_ms = excluded.updated_at_ms;
                        """;
                    BindKey(command, key);
                    command.Parameters.Add("$body", SqliteType.Blob).Value = bodyBytes;
                    command.Parameters.AddWithValue("$version", ToSqliteInteger(version, nameof(version)));
                    command.Parameters.AddWithValue("$updated_at_ms", updatedAt.ToUnixTimeMilliseconds());
                    command.ExecuteNonQuery();
                }

                WriteWatermark(key, version, transaction);
                WriteJournal(key, "upsert", version, bodyBytes, updatedAt, transaction);
                transaction.Commit();

                var document = new Document(key, body, version, updatedAt);
                var outcome = current is null ? WriteOutcome.Created : WriteOutcome.Updated;
                return ValueTask.FromResult(new StorageWriteResult(outcome, document));
            }
        }
    }

    /// <inheritdoc />
    public ValueTask<StorageWriteResult> DeleteAsync(
        DocumentKey key,
        DocumentPrecondition precondition,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            ThrowIfDisposed();
            lock (pathGate)
            {
                using var transaction = connection.BeginTransaction();
                var current = ReadCurrent(key, transaction);
                if (current is null)
                {
                    transaction.Rollback();
                    return ValueTask.FromResult(StorageWriteResult.NotFound);
                }

                if (!precondition.Matches(current))
                {
                    transaction.Rollback();
                    return ValueTask.FromResult(StorageWriteResult.Conflict);
                }

                var version = NextVersion(ReadWatermark(key, transaction));
                var deletedAt = DateTimeOffset.UtcNow;
                using (var command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText = """
                        DELETE FROM documents
                        WHERE tenant = $tenant AND collection = $collection AND document_id = $document_id;
                        """;
                    BindKey(command, key);
                    command.ExecuteNonQuery();
                }

                WriteWatermark(key, version, transaction);
                WriteJournal(key, "delete", version, body: null, deletedAt, transaction);
                transaction.Commit();
                return ValueTask.FromResult(new StorageWriteResult(WriteOutcome.Deleted, current));
            }
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<Document> ScanAsync(
        TenantId tenant,
        CollectionId collection,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Document[] snapshot;
        lock (gate)
        {
            ThrowIfDisposed();
            lock (pathGate)
            {
                using var command = connection.CreateCommand();
                command.CommandText = """
                    SELECT document_id, body, version, updated_at_ms
                    FROM documents
                    WHERE tenant = $tenant AND collection = $collection
                    ORDER BY document_id COLLATE BINARY;
                    """;
                command.Parameters.AddWithValue("$tenant", tenant.Value);
                command.Parameters.AddWithValue("$collection", collection.Value);
                using var reader = command.ExecuteReader();
                var documents = new List<Document>();
                while (reader.Read())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var key = new DocumentKey(tenant, collection, reader.GetString(0));
                    documents.Add(ReadDocument(key, reader, bodyOrdinal: 1, versionOrdinal: 2, updatedAtOrdinal: 3));
                }

                snapshot = documents.ToArray();
            }
        }

        foreach (var document in snapshot)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return document;
            await Task.Yield();
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        lock (gate)
        {
            if (disposed)
            {
                return ValueTask.CompletedTask;
            }

            disposed = true;
            lock (pathGate)
            {
                connection.Dispose();
            }
        }

        return ValueTask.CompletedTask;
    }

    private void ConfigureConnection()
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            PRAGMA journal_mode={JournalMode};
            PRAGMA synchronous=NORMAL;
            PRAGMA foreign_keys=ON;
            PRAGMA busy_timeout=60000;
            """;
        command.ExecuteNonQuery();
    }

    private void EnsureSchema()
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS schema_metadata (
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL
            ) WITHOUT ROWID;

            CREATE TABLE IF NOT EXISTS documents (
                tenant TEXT NOT NULL,
                collection TEXT NOT NULL,
                document_id TEXT NOT NULL,
                body BLOB NOT NULL,
                version INTEGER NOT NULL CHECK (version > 0),
                updated_at_ms INTEGER NOT NULL,
                PRIMARY KEY (tenant, collection, document_id)
            ) WITHOUT ROWID;

            CREATE TABLE IF NOT EXISTS version_watermarks (
                tenant TEXT NOT NULL,
                collection TEXT NOT NULL,
                document_id TEXT NOT NULL,
                version INTEGER NOT NULL CHECK (version > 0),
                PRIMARY KEY (tenant, collection, document_id)
            ) WITHOUT ROWID;

            CREATE TABLE IF NOT EXISTS publication_journal (
                sequence INTEGER PRIMARY KEY AUTOINCREMENT,
                operation TEXT NOT NULL,
                tenant TEXT NOT NULL,
                collection TEXT NOT NULL,
                document_id TEXT NOT NULL,
                version INTEGER NOT NULL CHECK (version > 0),
                body BLOB,
                updated_at_ms INTEGER NOT NULL
            );

            CREATE INDEX IF NOT EXISTS publication_journal_key
                ON publication_journal (tenant, collection, document_id, sequence);

            INSERT INTO schema_metadata (key, value)
            VALUES ('schema_version', $schema_version)
            ON CONFLICT (key) DO UPDATE SET value = excluded.value;
            """;
        command.Parameters.AddWithValue("$schema_version", SchemaVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();
    }

    private Document? ReadCurrent(DocumentKey key, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT body, version, updated_at_ms
            FROM documents
            WHERE tenant = $tenant AND collection = $collection AND document_id = $document_id;
            """;
        BindKey(command, key);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadDocument(key, reader) : null;
    }

    private long ReadWatermark(DocumentKey key, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT version
            FROM version_watermarks
            WHERE tenant = $tenant AND collection = $collection AND document_id = $document_id;
            """;
        BindKey(command, key);
        var value = command.ExecuteScalar();
        return value is null or DBNull ? 0 : Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private void WriteWatermark(DocumentKey key, ulong version, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO version_watermarks (tenant, collection, document_id, version)
            VALUES ($tenant, $collection, $document_id, $version)
            ON CONFLICT (tenant, collection, document_id) DO UPDATE SET version = excluded.version;
            """;
        BindKey(command, key);
        command.Parameters.AddWithValue("$version", ToSqliteInteger(version, nameof(version)));
        command.ExecuteNonQuery();
    }

    private void WriteJournal(
        DocumentKey key,
        string operation,
        ulong version,
        byte[]? body,
        DateTimeOffset updatedAt,
        SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO publication_journal
                (operation, tenant, collection, document_id, version, body, updated_at_ms)
            VALUES ($operation, $tenant, $collection, $document_id, $version, $body, $updated_at_ms);
            """;
        command.Parameters.AddWithValue("$operation", operation);
        BindKey(command, key);
        command.Parameters.AddWithValue("$version", ToSqliteInteger(version, nameof(version)));
        command.Parameters.Add("$body", SqliteType.Blob).Value = body ?? (object)DBNull.Value;
        command.Parameters.AddWithValue("$updated_at_ms", updatedAt.ToUnixTimeMilliseconds());
        command.ExecuteNonQuery();
    }

    private static Document ReadDocument(
        DocumentKey key,
        SqliteDataReader reader,
        int bodyOrdinal = 0,
        int versionOrdinal = 1,
        int updatedAtOrdinal = 2)
    {
        var body = JsonDocument.Parse((byte[])reader.GetValue(bodyOrdinal));
        using (body)
        {
            var version = Convert.ToUInt64(reader.GetValue(versionOrdinal), System.Globalization.CultureInfo.InvariantCulture);
            var updatedAt = DateTimeOffset.FromUnixTimeMilliseconds(
                Convert.ToInt64(reader.GetValue(updatedAtOrdinal), System.Globalization.CultureInfo.InvariantCulture));
            return new Document(key, body.RootElement, version, updatedAt);
        }
    }

    private static byte[] EncodeBody(JsonElement body)
    {
        if (body.ValueKind == JsonValueKind.Undefined)
        {
            throw new ArgumentException("A document body must be valid JSON.", nameof(body));
        }

        return Encoding.UTF8.GetBytes(body.GetRawText());
    }

    private static ulong NextVersion(long previousVersion)
    {
        if (previousVersion < 0 || previousVersion == MaximumSqliteInteger)
        {
            throw new OverflowException("The document version cannot be represented by SQLite.");
        }

        return checked((ulong)previousVersion + 1);
    }

    private static long ToSqliteInteger(ulong value, string parameterName)
    {
        if (value > MaximumSqliteInteger)
        {
            throw new OverflowException($"The {parameterName} value cannot be represented by SQLite.");
        }

        return (long)value;
    }

    private static void BindKey(SqliteCommand command, DocumentKey key)
    {
        command.Parameters.AddWithValue("$tenant", key.Tenant.Value);
        command.Parameters.AddWithValue("$collection", key.Collection.Value);
        command.Parameters.AddWithValue("$document_id", key.DocumentId);
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(disposed, this);
}

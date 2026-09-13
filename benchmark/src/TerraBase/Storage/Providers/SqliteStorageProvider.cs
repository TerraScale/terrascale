using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace TerraBase;

/// <summary>A transactional SQLite-backed document storage provider.</summary>
/// <remarks>
/// Documents and their version watermarks are committed in the same SQLite
/// transaction. The watermark table keeps versions monotonic across deletes,
/// and WAL mode permits readers to continue while the single writer commits.
/// </remarks>
public sealed class SqliteStorageProvider : IStorageProvider
{
    private const long MaximumSqliteVersion = long.MaxValue;
    private readonly object gate = new();
    private readonly SqliteConnection connection;
    private readonly string databasePath;
    private bool disposed;

    /// <summary>Opens or creates a SQLite database at the supplied path.</summary>
    /// <param name="path">The SQLite database file path.</param>
    public SqliteStorageProvider(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        databasePath = Path.GetFullPath(path);
        var parent = Path.GetDirectoryName(databasePath)
            ?? throw new ArgumentException("The database path has no parent directory.", nameof(path));
        Directory.CreateDirectory(parent);

        connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = false,
        }.ToString());
        connection.Open();
        try
        {
            ConfigureDatabase();
            EnsureSchema();
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    /// <summary>The fully qualified SQLite database path.</summary>
    public string DatabasePath => databasePath;

    /// <inheritdoc />
    public ValueTask<Document?> ReadAsync(DocumentKey key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            ThrowIfDisposed();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT tenant, collection, document_id, body, version, updated_at
                FROM documents
                WHERE tenant = $tenant AND collection = $collection AND document_id = $document_id;
                """;
            BindKey(command, key);
            using var reader = command.ExecuteReader();
            return ValueTask.FromResult(reader.Read() ? ReadDocument(reader) : null);
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
        if (body.ValueKind == JsonValueKind.Undefined)
        {
            throw new ArgumentException("A document body must be valid JSON.", nameof(body));
        }

        lock (gate)
        {
            ThrowIfDisposed();
            using var transaction = connection.BeginTransaction(SqliteTransactionMode.Immediate);
            var current = ReadDocument(key, transaction);
            if (!precondition.Matches(current))
            {
                transaction.Rollback();
                return ValueTask.FromResult(StorageWriteResult.Conflict);
            }

            var previousVersion = ReadVersionWatermark(key, transaction);
            var nextVersion = checked(previousVersion + 1);
            if (nextVersion > MaximumSqliteVersion)
            {
                throw new InvalidOperationException($"The document version for {key} exceeds SQLite's integer range.");
            }

            var document = new Document(key, body, nextVersion, DateTimeOffset.UtcNow);
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO documents (tenant, collection, document_id, body, version, updated_at)
                    VALUES ($tenant, $collection, $document_id, $body, $version, $updated_at)
                    ON CONFLICT (tenant, collection, document_id) DO UPDATE SET
                        body = excluded.body,
                        version = excluded.version,
                        updated_at = excluded.updated_at;
                    """;
                BindKey(command, key);
                command.Parameters.Add("$body", SqliteType.Blob).Value = Encoding.UTF8.GetBytes(body.GetRawText());
                command.Parameters.AddWithValue("$version", checked((long)nextVersion));
                command.Parameters.AddWithValue("$updated_at", document.UpdatedAt.ToString("O", CultureInfo.InvariantCulture));
                command.ExecuteNonQuery();
            }

            WriteVersionWatermark(key, nextVersion, transaction);
            transaction.Commit();
            var outcome = current is null ? WriteOutcome.Created : WriteOutcome.Updated;
            return ValueTask.FromResult(new StorageWriteResult(outcome, document));
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
            using var transaction = connection.BeginTransaction(SqliteTransactionMode.Immediate);
            var current = ReadDocument(key, transaction);
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

            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                DELETE FROM documents
                WHERE tenant = $tenant AND collection = $collection AND document_id = $document_id;
                """;
            BindKey(command, key);
            command.ExecuteNonQuery();
            transaction.Commit();
            return ValueTask.FromResult(new StorageWriteResult(WriteOutcome.Deleted, current));
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
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT tenant, collection, document_id, body, version, updated_at
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
                documents.Add(ReadDocument(reader));
            }

            snapshot = documents.ToArray();
        }

        foreach (var document in snapshot)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return document;
            await Task.CompletedTask.ConfigureAwait(false);
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
            connection.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private void ConfigureDatabase()
    {
        ExecutePragma("PRAGMA journal_mode = WAL;");
        ExecutePragma("PRAGMA synchronous = FULL;");
        ExecutePragma("PRAGMA foreign_keys = ON;");
        ExecutePragma("PRAGMA busy_timeout = 5000;");
    }

    private void EnsureSchema()
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS documents (
                tenant TEXT NOT NULL,
                collection TEXT NOT NULL,
                document_id TEXT NOT NULL,
                body BLOB NOT NULL,
                version INTEGER NOT NULL CHECK (version > 0),
                updated_at TEXT NOT NULL,
                PRIMARY KEY (tenant, collection, document_id)
            );

            CREATE TABLE IF NOT EXISTS version_watermarks (
                tenant TEXT NOT NULL,
                collection TEXT NOT NULL,
                document_id TEXT NOT NULL,
                version INTEGER NOT NULL CHECK (version > 0),
                PRIMARY KEY (tenant, collection, document_id)
            );
            """;
        command.ExecuteNonQuery();
    }

    private void ExecutePragma(string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static void BindKey(SqliteCommand command, DocumentKey key)
    {
        command.Parameters.AddWithValue("$tenant", key.Tenant.Value);
        command.Parameters.AddWithValue("$collection", key.Collection.Value);
        command.Parameters.AddWithValue("$document_id", key.DocumentId);
    }

    private Document? ReadDocument(DocumentKey key, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT tenant, collection, document_id, body, version, updated_at
            FROM documents
            WHERE tenant = $tenant AND collection = $collection AND document_id = $document_id;
            """;
        BindKey(command, key);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadDocument(reader) : null;
    }

    private ulong ReadVersionWatermark(DocumentKey key, SqliteTransaction transaction)
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
        return value is null or DBNull ? 0UL : ReadVersion(value, key);
    }

    private void WriteVersionWatermark(DocumentKey key, ulong version, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO version_watermarks (tenant, collection, document_id, version)
            VALUES ($tenant, $collection, $document_id, $version)
            ON CONFLICT (tenant, collection, document_id) DO UPDATE SET version = excluded.version;
            """;
        BindKey(command, key);
        command.Parameters.AddWithValue("$version", checked((long)version));
        command.ExecuteNonQuery();
    }

    private static Document ReadDocument(SqliteDataReader reader)
    {
        var tenant = new TenantId(reader.GetString(0));
        var collection = new CollectionId(reader.GetString(1));
        var key = new DocumentKey(tenant, collection, reader.GetString(2));
        var body = reader.GetFieldValue<byte[]>(3);
        var version = ReadVersion(reader.GetValue(4), key);
        var updatedAtText = reader.GetString(5);
        if (!DateTimeOffset.TryParse(
                updatedAtText,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var updatedAt))
        {
            throw new InvalidDataException($"The SQLite document {key} has an invalid update timestamp.");
        }

        try
        {
            using var parsed = JsonDocument.Parse(body);
            return new Document(key, parsed.RootElement, version, updatedAt);
        }
        catch (JsonException failure)
        {
            throw new InvalidDataException($"The SQLite document {key} contains invalid JSON.", failure);
        }
    }

    private static ulong ReadVersion(object value, DocumentKey key)
    {
        try
        {
            var version = Convert.ToInt64(value, CultureInfo.InvariantCulture);
            if (version <= 0)
            {
                throw new InvalidDataException($"The SQLite document {key} has an invalid version.");
            }

            return checked((ulong)version);
        }
        catch (OverflowException failure)
        {
            throw new InvalidDataException($"The SQLite document {key} has an invalid version.", failure);
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(disposed, this);
}

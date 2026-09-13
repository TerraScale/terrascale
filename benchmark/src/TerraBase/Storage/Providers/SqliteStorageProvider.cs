using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TerraBase.Storage.Schema;

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
public sealed class SqliteStorageProvider : IStorageProvider, ITransactionalStorageProvider
{
    private const long MaximumSqliteInteger = long.MaxValue;
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
            SqliteSchema.EnsureCreated(connection);
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
                    SELECT body, revision, updated_at_ms, deleted
                    FROM documents
                    WHERE tenant = $tenant AND collection = $collection AND document_id = $document_id
                    ORDER BY commit_seq DESC LIMIT 1;
                    """;
                BindKey(command, key);
                using var reader = command.ExecuteReader();
                return ValueTask.FromResult(reader.Read() && reader.GetInt64(3) == 0 ? ReadDocument(key, reader) : null);
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
                using var transaction = connection.BeginTransaction(deferred: false);
                var current = ReadCurrent(key, transaction);
                if (!precondition.Matches(current))
                {
                    transaction.Rollback();
                    return ValueTask.FromResult(StorageWriteResult.Conflict);
                }

                var previousVersion = ReadWatermark(key, transaction);
                var version = NextVersion(previousVersion);
                var commitSequence = NextCommitSequence(transaction);
                var updatedAt = DateTimeOffset.UtcNow;
                using (var command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText = """
                        INSERT INTO documents
                            (tenant, collection, id, document_id, commit_seq, commit_ulid, revision, deleted, body, context, version, updated_at, updated_at_ms)
                        VALUES ($tenant, $collection, $document_id, $document_id, $commit_seq, $commit_ulid, $version, 0, $body, $context, $version, $updated_at, $updated_at_ms);
                        """;
                    BindKey(command, key);
                    command.Parameters.AddWithValue("$commit_seq", ToSqliteInteger((ulong)commitSequence, nameof(commitSequence)));
                    command.Parameters.AddWithValue("$commit_ulid", Guid.NewGuid().ToString("N"));
                    command.Parameters.Add("$context", SqliteType.Blob).Value = Array.Empty<byte>();
                    command.Parameters.Add("$body", SqliteType.Blob).Value = bodyBytes;
                    command.Parameters.AddWithValue("$version", ToSqliteInteger(version, nameof(version)));
                    command.Parameters.AddWithValue("$updated_at", updatedAt.ToString("O"));
                    command.Parameters.AddWithValue("$updated_at_ms", updatedAt.ToUnixTimeMilliseconds());
                    command.ExecuteNonQuery();
                }

                WriteWatermark(key, version, transaction);
                WriteJournal(key, "upsert", version, bodyBytes, updatedAt, transaction);
                WriteCanonicalJournal(key, "upsert", commitSequence, bodyBytes, transaction);
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
                using var transaction = connection.BeginTransaction(deferred: false);
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
                var commitSequence = NextCommitSequence(transaction);
                var deletedAt = DateTimeOffset.UtcNow;
                using (var command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText = """
                        INSERT INTO documents
                            (tenant, collection, id, document_id, commit_seq, commit_ulid, revision, deleted, body, context, version, updated_at, updated_at_ms)
                        VALUES ($tenant, $collection, $document_id, $document_id, $commit_seq, $commit_ulid, $version, 1, $body, $context, $version, $updated_at, $updated_at_ms);
                        """;
                    BindKey(command, key);
                    command.Parameters.AddWithValue("$commit_seq", ToSqliteInteger((ulong)commitSequence, nameof(commitSequence)));
                    command.Parameters.AddWithValue("$version", ToSqliteInteger(version, nameof(version)));
                    command.Parameters.AddWithValue("$commit_ulid", Guid.NewGuid().ToString("N"));
                    command.Parameters.AddWithValue("$updated_at", deletedAt.ToString("O"));
                    command.Parameters.Add("$body", SqliteType.Blob).Value = Array.Empty<byte>();
                    command.Parameters.Add("$context", SqliteType.Blob).Value = Array.Empty<byte>();
                    command.Parameters.AddWithValue("$updated_at_ms", deletedAt.ToUnixTimeMilliseconds());
                    command.ExecuteNonQuery();
                }

                WriteWatermark(key, version, transaction);
                WriteJournal(key, "delete", version, body: null, deletedAt, transaction);
                WriteCanonicalJournal(key, "delete", commitSequence, Array.Empty<byte>(), transaction);
                transaction.Commit();
                return ValueTask.FromResult(new StorageWriteResult(WriteOutcome.Deleted, current));
            }
        }
    }

    /// <inheritdoc />
    public ValueTask<StorageBatchResult> ApplyBatchAsync(
        IReadOnlyList<StorageMutation> mutations,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mutations);
        if (mutations.Count == 0)
        {
            throw new ArgumentException("A storage batch must contain at least one mutation.", nameof(mutations));
        }

        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            ThrowIfDisposed();
            lock (pathGate)
            {
                using var transaction = connection.BeginTransaction(deferred: false);
                var writes = new List<StorageWriteResult>(mutations.Count);
                ulong? firstSequence = null;
                ulong? lastSequence = null;

                foreach (var mutation in mutations)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    ArgumentNullException.ThrowIfNull(mutation);

                    var current = ReadCurrent(mutation.Key, transaction);
                    if (mutation.Kind == StorageMutationKind.Upsert)
                    {
                        if (!mutation.Precondition.Matches(current))
                        {
                            transaction.Rollback();
                            return ValueTask.FromResult(StorageBatchResult.Conflict);
                        }

                        var body = EncodeBody(mutation.Body);
                        var version = NextVersion(ReadWatermark(mutation.Key, transaction));
                        var sequence = NextCommitSequence(transaction);
                        var updatedAt = DateTimeOffset.UtcNow;
                        WriteDocumentRow(mutation.Key, sequence, version, false, body, updatedAt, transaction);
                        WriteWatermark(mutation.Key, version, transaction);
                        WriteJournal(mutation.Key, "upsert", version, body, updatedAt, transaction);
                        WriteCanonicalJournal(mutation.Key, "upsert", sequence, body, transaction);
                        writes.Add(new StorageWriteResult(
                            current is null ? WriteOutcome.Created : WriteOutcome.Updated,
                            new Document(mutation.Key, mutation.Body, version, updatedAt)));
                        TrackSequence((ulong)sequence, ref firstSequence, ref lastSequence);
                        continue;
                    }

                    if (mutation.Kind != StorageMutationKind.Delete)
                    {
                        throw new ArgumentOutOfRangeException(nameof(mutations), mutation.Kind, "Unknown storage mutation kind.");
                    }

                    if (current is null)
                    {
                        transaction.Rollback();
                        return ValueTask.FromResult(StorageBatchResult.NotFound);
                    }

                    if (!mutation.Precondition.Matches(current))
                    {
                        transaction.Rollback();
                        return ValueTask.FromResult(StorageBatchResult.Conflict);
                    }

                    var deleteVersion = NextVersion(ReadWatermark(mutation.Key, transaction));
                    var deleteSequence = NextCommitSequence(transaction);
                    var deletedAt = DateTimeOffset.UtcNow;
                    WriteDocumentRow(mutation.Key, deleteSequence, deleteVersion, true, [], deletedAt, transaction);
                    WriteWatermark(mutation.Key, deleteVersion, transaction);
                    WriteJournal(mutation.Key, "delete", deleteVersion, body: null, deletedAt, transaction);
                    WriteCanonicalJournal(mutation.Key, "delete", deleteSequence, [], transaction);
                    writes.Add(new StorageWriteResult(WriteOutcome.Deleted, current));
                    TrackSequence((ulong)deleteSequence, ref firstSequence, ref lastSequence);
                }

                cancellationToken.ThrowIfCancellationRequested();
                transaction.Commit();
                return ValueTask.FromResult(new StorageBatchResult(
                    StorageBatchOutcome.Committed,
                    writes,
                    firstSequence,
                    lastSequence));
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
                    SELECT d.document_id, d.body, d.revision, d.updated_at_ms
                    FROM documents AS d
                    JOIN (SELECT tenant, collection, document_id, MAX(commit_seq) AS commit_seq
                          FROM documents WHERE tenant = $tenant AND collection = $collection
                          GROUP BY tenant, collection, document_id) AS latest
                      ON latest.tenant = d.tenant AND latest.collection = d.collection
                     AND latest.document_id = d.document_id AND latest.commit_seq = d.commit_seq
                    WHERE d.deleted = 0
                    ORDER BY d.document_id COLLATE BINARY;
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

    private Document? ReadCurrent(DocumentKey key, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT body, revision, updated_at_ms, deleted
            FROM documents
            WHERE tenant = $tenant AND collection = $collection AND document_id = $document_id
            ORDER BY commit_seq DESC LIMIT 1;
            """;
        BindKey(command, key);
        using var reader = command.ExecuteReader();
        return reader.Read() && reader.GetInt64(3) == 0 ? ReadDocument(key, reader) : null;
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

    private void WriteDocumentRow(
        DocumentKey key,
        long sequence,
        ulong version,
        bool deleted,
        byte[] body,
        DateTimeOffset updatedAt,
        SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO documents
                (tenant, collection, id, document_id, commit_seq, commit_ulid, revision, deleted, body, context, version, updated_at, updated_at_ms)
            VALUES ($tenant, $collection, $document_id, $document_id, $commit_seq, $commit_ulid, $version, $deleted, $body, $context, $version, $updated_at, $updated_at_ms);
            """;
        BindKey(command, key);
        command.Parameters.AddWithValue("$commit_seq", ToSqliteInteger((ulong)sequence, nameof(sequence)));
        command.Parameters.AddWithValue("$commit_ulid", Guid.NewGuid().ToString("N"));
        command.Parameters.AddWithValue("$version", ToSqliteInteger(version, nameof(version)));
        command.Parameters.AddWithValue("$deleted", deleted ? 1 : 0);
        command.Parameters.Add("$body", SqliteType.Blob).Value = body;
        command.Parameters.Add("$context", SqliteType.Blob).Value = Array.Empty<byte>();
        command.Parameters.AddWithValue("$updated_at", updatedAt.ToString("O"));
        command.Parameters.AddWithValue("$updated_at_ms", updatedAt.ToUnixTimeMilliseconds());
        command.ExecuteNonQuery();
    }

    private long NextCommitSequence(SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COALESCE(MAX(commit_seq), 0) + 1 FROM documents;";
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
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

    private void WriteCanonicalJournal(
        DocumentKey key,
        string operation,
        long sequence,
        byte[] frame,
        SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO journal (sequence, tenant, operation, frame)
            VALUES ($sequence, $tenant, $operation, $frame);
            """;
        command.Parameters.AddWithValue("$sequence", sequence);
        command.Parameters.AddWithValue("$tenant", key.Tenant.Value);
        command.Parameters.AddWithValue("$operation", operation);
        command.Parameters.Add("$frame", SqliteType.Blob).Value = frame;
        command.ExecuteNonQuery();
    }

    private static void TrackSequence(ulong sequence, ref ulong? firstSequence, ref ulong? lastSequence)
    {
        firstSequence ??= sequence;
        lastSequence = sequence;
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

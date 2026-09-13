using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;

namespace TerraBase;

/// <summary>The result of a document write or delete.</summary>
public enum WriteOutcome
{
    /// <summary>A new document was created.</summary>
    Created,

    /// <summary>An existing document was replaced.</summary>
    Updated,

    /// <summary>An existing document was deleted.</summary>
    Deleted,

    /// <summary>The expected version did not match.</summary>
    Conflict,

    /// <summary>The requested document did not exist.</summary>
    NotFound,
}

/// <summary>Controls the version precondition on one mutation.</summary>
public readonly record struct DocumentPrecondition
{
    private DocumentPrecondition(ulong? expectedVersion, bool mustExist)
    {
        ExpectedVersion = expectedVersion;
        MustExist = mustExist;
    }

    /// <summary>Accepts any current version, including an absent document.</summary>
    public static DocumentPrecondition Any { get; } = new(null, false);

    /// <summary>Requires that no document currently exists.</summary>
    public static DocumentPrecondition Missing { get; } = new(null, false) { MustBeMissing = true };

    /// <summary>Requires the supplied current version.</summary>
    /// <param name="version">The expected version.</param>
    /// <returns>A version precondition.</returns>
    public static DocumentPrecondition Version(ulong version) => new(version, true);

    /// <summary>The expected version, when this is a version precondition.</summary>
    public ulong? ExpectedVersion { get; }

    /// <summary>Whether an existing document is required.</summary>
    public bool MustExist { get; }

    /// <summary>Whether an absent document is required.</summary>
    public bool MustBeMissing { get; init; }

    internal bool Matches(Document? current) =>
        (MustBeMissing && current is null)
        || (!MustBeMissing && !MustExist)
        || (MustExist && current is not null && current.Version == ExpectedVersion);
}

/// <summary>An immutable document returned by a storage provider.</summary>
public sealed record Document
{
    /// <summary>Creates a document snapshot.</summary>
    /// <param name="key">The document key.</param>
    /// <param name="body">The JSON body.</param>
    /// <param name="version">The monotonic document version.</param>
    /// <param name="updatedAt">The update timestamp.</param>
    public Document(DocumentKey key, JsonElement body, ulong version, DateTimeOffset updatedAt)
    {
        if (body.ValueKind == JsonValueKind.Undefined)
        {
            throw new ArgumentException("A document body must be valid JSON.", nameof(body));
        }

        if (version == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(version), "Document versions start at one.");
        }

        Key = key;
        Body = body.Clone();
        Version = version;
        UpdatedAt = updatedAt;
    }

    /// <summary>The document key.</summary>
    public DocumentKey Key { get; }

    /// <summary>The immutable JSON body.</summary>
    public JsonElement Body { get; }

    /// <summary>The monotonic version assigned by the provider.</summary>
    public ulong Version { get; }

    /// <summary>The provider timestamp for the latest write.</summary>
    public DateTimeOffset UpdatedAt { get; }
}

/// <summary>The typed result of one storage mutation.</summary>
public sealed record StorageWriteResult(WriteOutcome Outcome, Document? Document)
{
    /// <summary>Creates a conflict result.</summary>
    public static StorageWriteResult Conflict { get; } = new(WriteOutcome.Conflict, null);

    /// <summary>Creates a not-found result.</summary>
    public static StorageWriteResult NotFound { get; } = new(WriteOutcome.NotFound, null);
}

/// <summary>
/// The storage port used by the engine. Implementations can use SQLite,
/// ZoneTree, or another durable backend without changing document operations.
/// </summary>
public interface IStorageProvider : IAsyncDisposable
{
    /// <summary>Reads one document snapshot.</summary>
    /// <param name="key">The document key.</param>
    /// <param name="cancellationToken">The cancellation signal.</param>
    /// <returns>The document, or <see langword="null"/> when absent.</returns>
    ValueTask<Document?> ReadAsync(DocumentKey key, CancellationToken cancellationToken = default);

    /// <summary>Creates or replaces one document.</summary>
    /// <param name="key">The document key.</param>
    /// <param name="body">The JSON body.</param>
    /// <param name="precondition">The version precondition.</param>
    /// <param name="cancellationToken">The cancellation signal.</param>
    /// <returns>The typed mutation result.</returns>
    ValueTask<StorageWriteResult> UpsertAsync(
        DocumentKey key,
        JsonElement body,
        DocumentPrecondition precondition,
        CancellationToken cancellationToken = default);

    /// <summary>Deletes one document.</summary>
    /// <param name="key">The document key.</param>
    /// <param name="precondition">The version precondition.</param>
    /// <param name="cancellationToken">The cancellation signal.</param>
    /// <returns>The typed mutation result.</returns>
    ValueTask<StorageWriteResult> DeleteAsync(
        DocumentKey key,
        DocumentPrecondition precondition,
        CancellationToken cancellationToken = default);

    /// <summary>Scans one tenant collection in deterministic key order.</summary>
    /// <param name="tenant">The owning tenant.</param>
    /// <param name="collection">The collection to scan.</param>
    /// <param name="cancellationToken">The cancellation signal.</param>
    /// <returns>The matching document snapshots.</returns>
    IAsyncEnumerable<Document> ScanAsync(
        TenantId tenant,
        CollectionId collection,
        CancellationToken cancellationToken = default);
}

/// <summary>The document operation abstraction exposed by the engine.</summary>
public interface IDocumentStore
{
    /// <summary>Reads one document.</summary>
    ValueTask<Document?> ReadAsync(DocumentKey key, CancellationToken cancellationToken = default);

    /// <summary>Creates or replaces one document.</summary>
    ValueTask<StorageWriteResult> UpsertAsync(
        DocumentKey key,
        JsonElement body,
        DocumentPrecondition precondition = default,
        CancellationToken cancellationToken = default);

    /// <summary>Deletes one document.</summary>
    ValueTask<StorageWriteResult> DeleteAsync(
        DocumentKey key,
        DocumentPrecondition precondition = default,
        CancellationToken cancellationToken = default);

    /// <summary>Scans one tenant collection.</summary>
    IAsyncEnumerable<Document> ScanAsync(
        TenantId tenant,
        CollectionId collection,
        CancellationToken cancellationToken = default);
}

/// <summary>A deterministic storage provider for local development and tests.</summary>
public sealed class InMemoryStorageProvider : IStorageProvider
{
    private readonly object gate = new();
    private readonly Dictionary<DocumentKey, Document> documents = new();
    private readonly Dictionary<DocumentKey, ulong> versionWatermarks = new();
    private bool disposed;

    /// <inheritdoc />
    public ValueTask<Document?> ReadAsync(DocumentKey key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            ThrowIfDisposed();
            return ValueTask.FromResult(documents.GetValueOrDefault(key));
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
        lock (gate)
        {
            ThrowIfDisposed();
            var current = documents.GetValueOrDefault(key);
            if (!precondition.Matches(current))
            {
                return ValueTask.FromResult(StorageWriteResult.Conflict);
            }

            var previousVersion = versionWatermarks.GetValueOrDefault(key);
            var version = checked(previousVersion + 1);
            var document = new Document(key, body, version, DateTimeOffset.UtcNow);
            documents[key] = document;
            versionWatermarks[key] = version;
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
            var current = documents.GetValueOrDefault(key);
            if (current is null)
            {
                return ValueTask.FromResult(StorageWriteResult.NotFound);
            }

            if (!precondition.Matches(current))
            {
                return ValueTask.FromResult(StorageWriteResult.Conflict);
            }

            documents.Remove(key);
            return ValueTask.FromResult(new StorageWriteResult(WriteOutcome.Deleted, current));
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<Document> ScanAsync(
        TenantId tenant,
        CollectionId collection,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Document[] snapshot;
        lock (gate)
        {
            ThrowIfDisposed();
            snapshot = documents
                .Where(pair => pair.Key.Tenant == tenant && pair.Key.Collection == collection)
                .OrderBy(pair => pair.Key.DocumentId, StringComparer.Ordinal)
                .Select(pair => pair.Value)
                .ToArray();
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
            disposed = true;
            documents.Clear();
            versionWatermarks.Clear();
        }

        return ValueTask.CompletedTask;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
    }
}

/// <summary>A durable file-backed storage provider for a local engine.</summary>
/// <remarks>
/// The provider keeps the current document set in memory for the operation
/// surface, and commits a complete versioned snapshot after each mutation.
/// Snapshot replacement happens through a same-directory temporary file so a
/// completed operation is always represented by one complete file.
/// </remarks>
public sealed class FileStorageProvider : IStorageProvider
{
    private const int StorageFormatVersion = 1;
    private static readonly JsonSerializerOptions serializerOptions = new(JsonSerializerDefaults.General);
    private static readonly ConcurrentDictionary<string, object> pathGates =
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private readonly object gate = new();
    private readonly Dictionary<DocumentKey, Document> documents = new();
    private readonly Dictionary<DocumentKey, ulong> versionWatermarks = new();
    private readonly string storagePath;
    private readonly object pathGate;
    private bool disposed;

    /// <summary>Opens a durable provider at the supplied file path.</summary>
    /// <param name="path">The file that stores the document snapshot.</param>
    public FileStorageProvider(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        storagePath = Path.GetFullPath(path);
        pathGate = pathGates.GetOrAdd(storagePath, static _ => new object());

        lock (pathGate)
        {
            if (Directory.Exists(storagePath))
            {
                throw new IOException($"The storage path is a directory: {storagePath}");
            }

            var parent = Path.GetDirectoryName(storagePath);
            if (parent is null)
            {
                throw new IOException($"The storage path has no parent directory: {storagePath}");
            }

            Directory.CreateDirectory(parent);
            if (File.Exists(storagePath))
            {
                try
                {
                    LoadSnapshot();
                    CleanupTemporarySnapshots();
                }
                catch (InvalidDataException failure)
                {
                    if (!TryRecoverSnapshot(failure))
                    {
                        throw;
                    }
                }
            }
            else if (!TryRecoverSnapshot())
            {
                PersistSnapshot();
            }
        }
    }

    /// <summary>The fully qualified file path used by this provider.</summary>
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
                LoadSnapshot();
                return ValueTask.FromResult(documents.GetValueOrDefault(key));
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
        lock (gate)
        {
            ThrowIfDisposed();
            lock (pathGate)
            {
                LoadSnapshot();
                var current = documents.GetValueOrDefault(key);
                if (!precondition.Matches(current))
                {
                    return ValueTask.FromResult(StorageWriteResult.Conflict);
                }

                var previousVersion = versionWatermarks.GetValueOrDefault(key);
                var document = new Document(key, body, checked(previousVersion + 1), DateTimeOffset.UtcNow);
                documents[key] = document;
                versionWatermarks[key] = document.Version;
                try
                {
                    PersistSnapshot();
                }
                catch
                {
                    Restore(key, current, previousVersion);
                    throw;
                }

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
                LoadSnapshot();
                var current = documents.GetValueOrDefault(key);
                if (current is null)
                {
                    return ValueTask.FromResult(StorageWriteResult.NotFound);
                }

                if (!precondition.Matches(current))
                {
                    return ValueTask.FromResult(StorageWriteResult.Conflict);
                }

                documents.Remove(key);
                try
                {
                    PersistSnapshot();
                }
                catch
                {
                    documents[key] = current;
                    throw;
                }

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
        Document[] snapshot;
        lock (gate)
        {
            ThrowIfDisposed();
            lock (pathGate)
            {
                LoadSnapshot();
                snapshot = documents
                    .Where(pair => pair.Key.Tenant == tenant && pair.Key.Collection == collection)
                    .OrderBy(pair => pair.Key.DocumentId, StringComparer.Ordinal)
                    .Select(pair => pair.Value)
                    .ToArray();
            }
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
            disposed = true;
            documents.Clear();
            versionWatermarks.Clear();
        }

        return ValueTask.CompletedTask;
    }

    private void LoadSnapshot() => ApplySnapshot(ReadSnapshot(storagePath));

    private static SnapshotState ReadSnapshot(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var snapshot = JsonSerializer.Deserialize<StorageSnapshot>(stream, serializerOptions)
                ?? throw new InvalidDataException("The storage snapshot is empty.");
            if (snapshot.FormatVersion != StorageFormatVersion)
            {
                throw new InvalidDataException($"Unsupported storage snapshot format {snapshot.FormatVersion}.");
            }

            if (snapshot.Documents is null)
            {
                throw new InvalidDataException("The storage snapshot documents collection is null.");
            }

            var loaded = new Dictionary<DocumentKey, Document>();
            var watermarks = new Dictionary<DocumentKey, ulong>();
            foreach (var persisted in snapshot.Documents)
            {
                if (persisted is null)
                {
                    throw new InvalidDataException("The storage snapshot contains a null document.");
                }

                var key = new DocumentKey(
                    new TenantId(persisted.Tenant),
                    new CollectionId(persisted.Collection),
                    persisted.DocumentId);
                if (loaded.ContainsKey(key))
                {
                    throw new InvalidDataException($"The storage snapshot contains duplicate key {key}.");
                }

                if (persisted.Body is null)
                {
                    throw new InvalidDataException($"The storage snapshot contains a null body for {key}.");
                }

                using var body = JsonDocument.Parse(persisted.Body);
                var document = new Document(key, body.RootElement, persisted.Version, persisted.UpdatedAt);
                loaded.Add(key, document);
                watermarks.Add(key, document.Version);
            }

            if (snapshot.VersionWatermarks is not null)
            {
                foreach (var persisted in snapshot.VersionWatermarks)
                {
                    if (persisted is null)
                    {
                        throw new InvalidDataException("The storage snapshot contains a null version watermark.");
                    }

                    if (persisted.Version == 0)
                    {
                        throw new InvalidDataException("The storage snapshot contains a zero version watermark.");
                    }

                    var key = new DocumentKey(
                        new TenantId(persisted.Tenant),
                        new CollectionId(persisted.Collection),
                        persisted.DocumentId);
                    if (watermarks.TryGetValue(key, out var currentVersion))
                    {
                        if (loaded.ContainsKey(key))
                        {
                            if (persisted.Version < currentVersion)
                            {
                                throw new InvalidDataException($"The storage snapshot contains a stale version watermark for {key}.");
                            }

                            watermarks[key] = persisted.Version;
                            continue;
                        }

                        throw new InvalidDataException($"The storage snapshot contains duplicate version watermarks for {key}.");
                    }

                    watermarks.Add(key, persisted.Version);
                }
            }

            return new SnapshotState(loaded, watermarks);
        }
        catch (JsonException failure)
        {
            throw new InvalidDataException($"The storage snapshot is not valid JSON: {path}", failure);
        }
        catch (ArgumentException failure)
        {
            throw new InvalidDataException($"The storage snapshot contains an invalid document: {path}", failure);
        }
        catch (InvalidOperationException failure)
        {
            throw new InvalidDataException($"The storage snapshot contains an invalid document: {path}", failure);
        }
    }

    private bool TryRecoverSnapshot(InvalidDataException? primaryFailure = null)
    {
        var candidates = EnumerateTemporarySnapshots().ToArray();
        if (candidates.Length == 0)
        {
            if (primaryFailure is not null)
            {
                throw primaryFailure;
            }

            return false;
        }

        InvalidDataException? candidateFailure = null;
        foreach (var candidate in candidates)
        {
            try
            {
                var recovered = ReadSnapshot(candidate);
                File.Move(candidate, storagePath, overwrite: true);
                ApplySnapshot(recovered);
                CleanupTemporarySnapshots();
                return true;
            }
            catch (InvalidDataException failure)
            {
                candidateFailure ??= failure;
            }
        }

        throw new InvalidDataException(
            $"The storage snapshot could not be recovered: {storagePath}",
            primaryFailure ?? candidateFailure);
    }

    private IEnumerable<string> EnumerateTemporarySnapshots() =>
        Directory.EnumerateFiles(
                Path.GetDirectoryName(storagePath)!,
                "*",
                SearchOption.TopDirectoryOnly)
            .Where(path =>
                path.StartsWith($"{storagePath}.", StringComparison.Ordinal)
                && path.EndsWith(".tmp", StringComparison.Ordinal))
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .ThenBy(path => path, StringComparer.Ordinal);

    private void CleanupTemporarySnapshots()
    {
        foreach (var candidate in EnumerateTemporarySnapshots())
        {
            try
            {
                File.Delete(candidate);
            }
            catch (FileNotFoundException)
            {
            }
        }
    }

    private void ApplySnapshot(SnapshotState snapshot)
    {
        documents.Clear();
        foreach (var (key, document) in snapshot.Documents)
        {
            documents.Add(key, document);
        }

        versionWatermarks.Clear();
        foreach (var (key, version) in snapshot.VersionWatermarks)
        {
            versionWatermarks.Add(key, version);
        }
    }

    private void PersistSnapshot()
    {
        var snapshot = new StorageSnapshot(
            StorageFormatVersion,
            documents
                .OrderBy(pair => pair.Key.Tenant.Value, StringComparer.Ordinal)
                .ThenBy(pair => pair.Key.Collection.Value, StringComparer.Ordinal)
                .ThenBy(pair => pair.Key.DocumentId, StringComparer.Ordinal)
                .Select(pair => new PersistedDocument(
                    pair.Key.Tenant.Value,
                    pair.Key.Collection.Value,
                    pair.Key.DocumentId,
                    pair.Value.Body.GetRawText(),
                    pair.Value.Version,
                    pair.Value.UpdatedAt))
                .ToArray(),
            versionWatermarks
                .Where(pair => !documents.ContainsKey(pair.Key))
                .OrderBy(pair => pair.Key.Tenant.Value, StringComparer.Ordinal)
                .ThenBy(pair => pair.Key.Collection.Value, StringComparer.Ordinal)
                .ThenBy(pair => pair.Key.DocumentId, StringComparer.Ordinal)
                .Select(pair => new PersistedVersion(
                    pair.Key.Tenant.Value,
                    pair.Key.Collection.Value,
                    pair.Key.DocumentId,
                    pair.Value))
                .ToArray());
        var temporaryPath = $"{storagePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 16 * 1024,
                FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, snapshot, serializerOptions);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, storagePath, overwrite: true);
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (FileNotFoundException)
            {
            }
        }
    }

    private void Restore(DocumentKey key, Document? current, ulong previousVersion)
    {
        if (current is null)
        {
            documents.Remove(key);
        }
        else
        {
            documents[key] = current;
        }

        if (previousVersion == 0)
        {
            versionWatermarks.Remove(key);
        }
        else
        {
            versionWatermarks[key] = previousVersion;
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
    }

    private sealed record SnapshotState(
        Dictionary<DocumentKey, Document> Documents,
        Dictionary<DocumentKey, ulong> VersionWatermarks);

    private sealed record StorageSnapshot(
        int FormatVersion,
        IReadOnlyList<PersistedDocument>? Documents,
        IReadOnlyList<PersistedVersion>? VersionWatermarks);

    private sealed record PersistedDocument(
        string Tenant,
        string Collection,
        string DocumentId,
        string Body,
        ulong Version,
        DateTimeOffset UpdatedAt);

    private sealed record PersistedVersion(
        string Tenant,
        string Collection,
        string DocumentId,
        ulong Version);
}

/// <summary>A document store that owns the engine's storage port.</summary>
public sealed class DocumentStore : IDocumentStore
{
    private readonly IStorageProvider provider;
    private readonly MetricsRegistry metrics;

    /// <summary>Creates a store over an abstract storage provider.</summary>
    /// <param name="provider">The storage provider.</param>
    /// <param name="metrics">The metrics registry receiving operation facts.</param>
    public DocumentStore(IStorageProvider provider, MetricsRegistry metrics)
    {
        this.provider = provider ?? throw new ArgumentNullException(nameof(provider));
        this.metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
    }

    /// <inheritdoc />
    public async ValueTask<Document?> ReadAsync(DocumentKey key, CancellationToken cancellationToken = default)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            var result = await provider.ReadAsync(key, cancellationToken).ConfigureAwait(false);
            metrics.RecordRequest(Region.Unknown, Operation.Read, result is null ? Outcome.NotFound : Outcome.Success, Stopwatch.GetElapsedTime(started));
            return result;
        }
        catch (OperationCanceledException)
        {
            metrics.RecordRequest(Region.Unknown, Operation.Read, Outcome.Timeout, Stopwatch.GetElapsedTime(started));
            throw;
        }
        catch
        {
            metrics.RecordRequest(Region.Unknown, Operation.Read, Outcome.Error, Stopwatch.GetElapsedTime(started));
            throw;
        }
    }

    /// <inheritdoc />
    public async ValueTask<StorageWriteResult> UpsertAsync(
        DocumentKey key,
        JsonElement body,
        DocumentPrecondition precondition = default,
        CancellationToken cancellationToken = default)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            var result = await provider.UpsertAsync(key, body, precondition == default ? DocumentPrecondition.Any : precondition, cancellationToken).ConfigureAwait(false);
            metrics.RecordRequest(Region.Unknown, Operation.Write, ToMetricOutcome(result.Outcome), Stopwatch.GetElapsedTime(started));
            return result;
        }
        catch (OperationCanceledException)
        {
            metrics.RecordRequest(Region.Unknown, Operation.Write, Outcome.Timeout, Stopwatch.GetElapsedTime(started));
            throw;
        }
        catch
        {
            metrics.RecordRequest(Region.Unknown, Operation.Write, Outcome.Error, Stopwatch.GetElapsedTime(started));
            throw;
        }
    }

    /// <inheritdoc />
    public async ValueTask<StorageWriteResult> DeleteAsync(
        DocumentKey key,
        DocumentPrecondition precondition = default,
        CancellationToken cancellationToken = default)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            var result = await provider.DeleteAsync(key, precondition == default ? DocumentPrecondition.Any : precondition, cancellationToken).ConfigureAwait(false);
            metrics.RecordRequest(Region.Unknown, Operation.Delete, ToMetricOutcome(result.Outcome), Stopwatch.GetElapsedTime(started));
            return result;
        }
        catch (OperationCanceledException)
        {
            metrics.RecordRequest(Region.Unknown, Operation.Delete, Outcome.Timeout, Stopwatch.GetElapsedTime(started));
            throw;
        }
        catch
        {
            metrics.RecordRequest(Region.Unknown, Operation.Delete, Outcome.Error, Stopwatch.GetElapsedTime(started));
            throw;
        }
    }

    /// <inheritdoc />
    public IAsyncEnumerable<Document> ScanAsync(
        TenantId tenant,
        CollectionId collection,
        CancellationToken cancellationToken = default) => provider.ScanAsync(tenant, collection, cancellationToken);

    /// <summary>Releases the underlying storage provider.</summary>
    public ValueTask DisposeAsync() => provider.DisposeAsync();

    private static Outcome ToMetricOutcome(WriteOutcome outcome) => outcome switch
    {
        WriteOutcome.Conflict => Outcome.Conflict,
        WriteOutcome.NotFound => Outcome.NotFound,
        _ => Outcome.Success,
    };
}

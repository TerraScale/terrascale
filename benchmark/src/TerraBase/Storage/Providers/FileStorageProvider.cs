using System.Collections.Concurrent;
using System.Text.Json;

namespace TerraBase;

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

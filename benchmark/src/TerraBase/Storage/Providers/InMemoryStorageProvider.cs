using System.Text.Json;

namespace TerraBase;

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

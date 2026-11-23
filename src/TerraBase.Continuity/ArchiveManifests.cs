using System.Collections.Concurrent;

namespace TerraBase.Continuity;

internal readonly struct DatabaseGateLease(SemaphoreSlim gate) : IDisposable
{
    public void Dispose() => gate.Release();
}

internal sealed record ArchiveObjectsDeleted;

internal readonly union ArchiveObjectsDeletedOutcome(ArchiveObjectsDeleted, ContinuityUnavailable);

internal sealed record ArchivePurged(bool RemovedObjects);

internal readonly union ArchivePurgeOutcome(ArchivePurged, ContinuityUnavailable);

public sealed class ArchiveManifests(S3ArchiveStore store, ContinuityMetrics metrics, TimeProvider timeProvider)
{
    private readonly ConcurrentDictionary<Ulid, ArchiveManifest> _cache = new();
    private readonly ConcurrentDictionary<Ulid, SemaphoreSlim> _gates = new();

    public bool IsEnabled => store.IsEnabled;

    internal S3ArchiveStore Store => store;

    internal async ValueTask<DatabaseGateLease> AcquireAsync(Ulid databaseId, CancellationToken cancellationToken)
    {
        var gate = _gates.GetOrAdd(databaseId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        return new DatabaseGateLease(gate);
    }

    internal async ValueTask<ArchiveManifestOutcome> LoadAsync(Ulid databaseId, CancellationToken cancellationToken)
    {
        if (_cache.TryGetValue(databaseId, out var cached))
        {
            return cached;
        }

        var fetched = await store.GetBytesAsync(ArchiveKeys.Manifest(databaseId), cancellationToken);
        var loaded = fetched switch
        {
            byte[] json => Decoded(ManifestCodec.Deserialize(databaseId, json)),
            ArchiveObjectMissing => ArchiveManifest.Empty(databaseId, timeProvider.GetUtcNow()),
            ArchiveUnavailable unavailable => new ContinuityUnavailable(unavailable.Message, unavailable.Code),
            ArchiveTimedOut timeout => new ContinuityUnavailable(timeout.Message, timeout.Code),
        };
        return loaded switch
        {
            ArchiveManifest manifest => _cache.GetOrAdd(databaseId, manifest),
            ContinuityUnavailable unavailable => unavailable,
        };

        static ArchiveManifestOutcome Decoded(ArchiveManifestDecodeOutcome decoded) => decoded switch
        {
            ArchiveManifest manifest => manifest,
            ArchiveManifestCorrupted corrupted => new ContinuityUnavailable(corrupted.Message, corrupted.Code),
        };
    }

    internal async ValueTask<ArchiveManifestOutcome> SaveAsync(ArchiveManifest manifest, CancellationToken cancellationToken)
    {
        if (!Ulid.TryParse(manifest.DatabaseId, out var databaseId))
        {
            throw new ArgumentException("manifest database id is not a ULID", nameof(manifest));
        }

        var updated = manifest with { UpdatedAtUtc = timeProvider.GetUtcNow() };
        var json = ManifestCodec.Serialize(updated);
        var stored = await store.PutBytesAsync(ArchiveKeys.Manifest(databaseId), json, ManifestCodec.JsonContentType, cancellationToken);
        return stored switch
        {
            ArchiveObjectWritten => Stored(databaseId, updated, json.Length),
            ArchiveUnavailable unavailable => new ContinuityUnavailable(unavailable.Message, unavailable.Code),
            ArchiveTimedOut timeout => new ContinuityUnavailable(timeout.Message, timeout.Code),
        };
    }

    internal async ValueTask<ArchivePurgeOutcome> PurgeAsync(Ulid databaseId, CancellationToken cancellationToken)
    {
        _cache.TryRemove(databaseId, out _);
        var listed = await store.ListKeysAsync(ArchiveKeys.DatabasePrefix(databaseId), cancellationToken);
        return listed switch
        {
            IReadOnlyList<string> keys => await DeleteAllAsync(keys, cancellationToken),
            ArchiveUnavailable unavailable => new ContinuityUnavailable(unavailable.Message, unavailable.Code),
            ArchiveTimedOut timeout => new ContinuityUnavailable(timeout.Message, timeout.Code),
        };
    }

    internal async ValueTask<ArchiveObjectsDeletedOutcome> DeleteObjectsAsync(IEnumerable<string> keys, CancellationToken cancellationToken)
    {
        foreach (var key in keys)
        {
            var deleted = await store.DeleteAsync(key, cancellationToken);
            var failure = deleted switch
            {
                ArchiveObjectWritten => (ContinuityUnavailable?)null,
                ArchiveUnavailable unavailable => new ContinuityUnavailable(unavailable.Message, unavailable.Code),
                ArchiveTimedOut timeout => new ContinuityUnavailable(timeout.Message, timeout.Code),
            };
            if (failure is not null)
            {
                return failure;
            }
        }

        return new ArchiveObjectsDeleted();
    }

    internal void Forget(Ulid databaseId) => _cache.TryRemove(databaseId, out _);

    private async ValueTask<ArchivePurgeOutcome> DeleteAllAsync(IReadOnlyList<string> keys, CancellationToken cancellationToken)
    {
        var deleted = await DeleteObjectsAsync(keys, cancellationToken);
        return deleted switch
        {
            ArchiveObjectsDeleted => new ArchivePurged(keys.Count > 0),
            ContinuityUnavailable unavailable => unavailable,
        };
    }

    private ArchiveManifest Stored(Ulid databaseId, ArchiveManifest manifest, long bytes)
    {
        _cache[databaseId] = manifest;
        metrics.Uploaded(ArchiveObjectKind.Manifest, bytes);
        return manifest;
    }
}

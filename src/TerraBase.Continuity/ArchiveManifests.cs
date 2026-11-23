using System.Collections.Concurrent;

namespace TerraBase.Continuity;

internal readonly struct DatabaseGateLease(SemaphoreSlim gate) : IDisposable
{
    public void Dispose() => gate.Release();
}

public sealed class ArchiveManifests(S3ArchiveStore store, ContinuityMetrics metrics, TimeProvider timeProvider)
{
    private readonly ConcurrentDictionary<Ulid, ArchiveManifest> _cache = new();
    private readonly ConcurrentDictionary<Ulid, SemaphoreSlim> _gates = new();

    public bool IsEnabled => store.IsEnabled;

    internal S3ArchiveStore Store => store;

    internal async ValueTask<DatabaseGateLease> AcquireAsync(Ulid databaseId, CancellationToken cancellationToken)
    {
        var gate = _gates.GetOrAdd(databaseId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new DatabaseGateLease(gate);
    }

    internal async ValueTask<Result<ArchiveManifest, Unavailable>> LoadAsync(Ulid databaseId, CancellationToken cancellationToken)
    {
        if (_cache.TryGetValue(databaseId, out var cached))
        {
            return cached;
        }

        Result<ArchiveManifest, Unavailable> loaded = await store.GetBytesAsync(ArchiveKeys.Manifest(databaseId), cancellationToken).ConfigureAwait(false) switch
        {
            byte[] json => ManifestCodec.Deserialize(databaseId, json),
            NotFound => ArchiveManifest.Empty(databaseId, timeProvider.GetUtcNow()),
            Unavailable unavailable => unavailable,
            RequestTimeout timeout => new Unavailable(timeout.Message, timeout.Code),
        };
        return loaded switch
        {
            ArchiveManifest manifest => _cache.GetOrAdd(databaseId, manifest),
            Unavailable unavailable => unavailable,
        };
    }

    internal async ValueTask<Result<ArchiveManifest, Unavailable>> SaveAsync(ArchiveManifest manifest, CancellationToken cancellationToken)
    {
        if (!Ulid.TryParse(manifest.DatabaseId, out var databaseId))
        {
            throw new ArgumentException("manifest database id is not a ULID", nameof(manifest));
        }

        var updated = manifest with { UpdatedAtUtc = timeProvider.GetUtcNow() };
        var json = ManifestCodec.Serialize(updated);
        return await store.PutBytesAsync(ArchiveKeys.Manifest(databaseId), json, ManifestCodec.JsonContentType, cancellationToken).ConfigureAwait(false) switch
        {
            Done => Stored(databaseId, updated, json.Length),
            Unavailable unavailable => unavailable,
            RequestTimeout timeout => new Unavailable(timeout.Message, timeout.Code),
        };
    }

    internal async ValueTask<Result<bool, Unavailable>> PurgeAsync(Ulid databaseId, CancellationToken cancellationToken)
    {
        _cache.TryRemove(databaseId, out _);
        var listed = await store.ListKeysAsync(ArchiveKeys.DatabasePrefix(databaseId), cancellationToken).ConfigureAwait(false);
        return listed switch
        {
            IReadOnlyList<string> keys => await DeleteAllAsync(keys, cancellationToken).ConfigureAwait(false),
            Unavailable unavailable => unavailable,
            RequestTimeout timeout => new Unavailable(timeout.Message, timeout.Code),
        };
    }

    internal async ValueTask<Result<Done, Unavailable>> DeleteObjectsAsync(IEnumerable<string> keys, CancellationToken cancellationToken)
    {
        foreach (var key in keys)
        {
            var deleted = await store.DeleteAsync(key, cancellationToken).ConfigureAwait(false);
            Unavailable? failure = deleted switch
            {
                Done => null,
                Unavailable unavailable => unavailable,
                RequestTimeout timeout => new Unavailable(timeout.Message, timeout.Code),
            };
            if (failure is not null)
            {
                return failure;
            }
        }

        return new Done();
    }

    internal void Forget(Ulid databaseId) => _cache.TryRemove(databaseId, out _);

    private async ValueTask<Result<bool, Unavailable>> DeleteAllAsync(IReadOnlyList<string> keys, CancellationToken cancellationToken) =>
        await DeleteObjectsAsync(keys, cancellationToken).ConfigureAwait(false) switch
        {
            Done => keys.Count > 0,
            Unavailable unavailable => unavailable,
        };

    private ArchiveManifest Stored(Ulid databaseId, ArchiveManifest manifest, long bytes)
    {
        _cache[databaseId] = manifest;
        metrics.Uploaded(ArchiveObjectKind.Manifest, bytes);
        return manifest;
    }
}

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

internal sealed record CachedManifest(ArchiveManifest Manifest, string? ETag);

internal readonly union CachedManifestOutcome(CachedManifest, ContinuityUnavailable);

public sealed class ArchiveManifests(S3ArchiveStore store, ContinuityMetrics metrics, TimeProvider timeProvider)
{
    private const int MaxCompareAndSwapAttempts = 5;

    private readonly ConcurrentDictionary<Ulid, CachedManifest> _cache = new();
    private readonly ConcurrentDictionary<Ulid, SemaphoreSlim> _gates = new();

    public bool IsEnabled => store.IsEnabled;

    internal S3ArchiveStore Store => store;

    internal async ValueTask<DatabaseGateLease> AcquireAsync(Ulid databaseId, CancellationToken cancellationToken)
    {
        var gate = _gates.GetOrAdd(databaseId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        return new DatabaseGateLease(gate);
    }

    internal async ValueTask<ArchiveManifestOutcome> ReadCurrentAsync(Ulid databaseId, CancellationToken cancellationToken)
    {
        var loaded = await FetchRemoteAsync(databaseId, cancellationToken);
        return loaded switch
        {
            CachedManifest fresh => fresh.Manifest,
            ContinuityUnavailable unavailable => unavailable,
        };
    }

    internal async ValueTask<ArchiveManifestOutcome> LoadAsync(Ulid databaseId, CancellationToken cancellationToken)
    {
        if (_cache.TryGetValue(databaseId, out var cached))
        {
            return cached.Manifest;
        }

        var loaded = await FetchRemoteAsync(databaseId, cancellationToken);
        return loaded switch
        {
            CachedManifest fresh => _cache.GetOrAdd(databaseId, fresh).Manifest,
            ContinuityUnavailable unavailable => unavailable,
        };
    }

    internal async ValueTask<ArchiveManifestOutcome> UpdateAsync(
        Ulid databaseId,
        Func<ArchiveManifest, ArchiveManifest> transform,
        CancellationToken cancellationToken)
    {
        CachedManifest current;
        if (_cache.TryGetValue(databaseId, out var cached))
        {
            current = cached;
        }
        else
        {
            var fetched = await FetchRemoteAsync(databaseId, cancellationToken);
            if (fetched switch { CachedManifest => null, ContinuityUnavailable unavailable => unavailable } is { } loadError)
            {
                return loadError;
            }

            current = fetched switch { CachedManifest loaded => loaded, ContinuityUnavailable => throw new InvalidOperationException() };
            _cache[databaseId] = current;
        }

        for (var attempt = 0; attempt < MaxCompareAndSwapAttempts; attempt++)
        {
            var updated = transform(current.Manifest) with { UpdatedAtUtc = timeProvider.GetUtcNow() };
            var json = ManifestCodec.Serialize(updated);
            var stored = await store.PutBytesIfMatchAsync(ArchiveKeys.Manifest(databaseId), json, ManifestCodec.JsonContentType, current.ETag, cancellationToken);
            if (stored switch { ArchiveConditionalWritten written => written, ArchivePreconditionFailed or ArchiveUnavailable or ArchiveTimedOut => null } is { } saved)
            {
                return Stored(databaseId, updated, saved.ETag, json.Length);
            }

            var nonConflict = stored switch
            {
                ArchiveConditionalWritten or ArchivePreconditionFailed => null,
                ArchiveUnavailable unavailable => new ContinuityUnavailable(unavailable.Message, unavailable.Code),
                ArchiveTimedOut timeout => new ContinuityUnavailable(timeout.Message, timeout.Code),
            };
            if (nonConflict is not null)
            {
                return nonConflict;
            }

            var reloaded = await FetchRemoteAsync(databaseId, cancellationToken);
            if (reloaded switch { CachedManifest => null, ContinuityUnavailable unavailable => unavailable } is { } reloadError)
            {
                return reloadError;
            }

            current = reloaded switch { CachedManifest fresh => fresh, ContinuityUnavailable => throw new InvalidOperationException() };
            _cache[databaseId] = current;
        }

        return new ContinuityUnavailable("archive manifest changed concurrently; retry", "archive_manifest_conflict");
    }

    internal ValueTask<ArchiveManifestOutcome> SaveAsync(ArchiveManifest manifest, CancellationToken cancellationToken)
    {
        if (!Ulid.TryParse(manifest.DatabaseId, out var databaseId))
        {
            throw new ArgumentException("manifest database id is not a ULID", nameof(manifest));
        }

        return UpdateAsync(databaseId, remote => Merge(remote, manifest), cancellationToken);
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

    private async ValueTask<CachedManifestOutcome> FetchRemoteAsync(Ulid databaseId, CancellationToken cancellationToken)
    {
        var fetched = await store.GetBytesWithETagAsync(ArchiveKeys.Manifest(databaseId), cancellationToken);
        return fetched switch
        {
            ArchiveVersionedBytes versioned => Decode(databaseId, versioned),
            ArchiveObjectMissing => new CachedManifest(ArchiveManifest.Empty(databaseId, timeProvider.GetUtcNow()), null),
            ArchiveUnavailable unavailable => new ContinuityUnavailable(unavailable.Message, unavailable.Code),
            ArchiveTimedOut timeout => new ContinuityUnavailable(timeout.Message, timeout.Code),
        };

        static CachedManifestOutcome Decode(Ulid id, ArchiveVersionedBytes versioned) => ManifestCodec.Deserialize(id, versioned.Content) switch
        {
            ArchiveManifest manifest => new CachedManifest(manifest, versioned.ETag),
            ArchiveManifestCorrupted corrupted => new ContinuityUnavailable(corrupted.Message, corrupted.Code),
        };
    }

    private static ArchiveManifest Merge(ArchiveManifest remote, ArchiveManifest local)
    {
        Dictionary<string, ArchivedSnapshot> snapshots = new(StringComparer.Ordinal);
        foreach (var snapshot in remote.Snapshots)
        {
            snapshots[snapshot.Key] = snapshot;
        }

        foreach (var snapshot in local.Snapshots)
        {
            snapshots[snapshot.Key] = snapshot;
        }

        Dictionary<string, ArchivedSegment> segments = new(StringComparer.Ordinal);
        foreach (var segment in remote.Segments)
        {
            segments[segment.Key] = segment;
        }

        foreach (var segment in local.Segments)
        {
            segments[segment.Key] = segment;
        }

        Dictionary<string, ArchivedOperation> operations = new(StringComparer.Ordinal);
        foreach (var operation in remote.Operations)
        {
            operations[operation.OperationId] = operation;
        }

        foreach (var operation in local.Operations)
        {
            operations[operation.OperationId] = operation;
        }

        return remote with
        {
            Snapshots = [.. snapshots.Values.OrderBy(static item => item.TakenAtUtc)],
            Segments = [.. segments.Values.OrderBy(static item => item.Origin, StringComparer.Ordinal).ThenBy(static item => item.FirstSequence)],
            Operations = [.. operations.Values.OrderBy(static item => item.StartedAtUtc)],
        };
    }

    private async ValueTask<ArchivePurgeOutcome> DeleteAllAsync(IReadOnlyList<string> keys, CancellationToken cancellationToken)
    {
        var deleted = await DeleteObjectsAsync(keys, cancellationToken);
        return deleted switch
        {
            ArchiveObjectsDeleted => new ArchivePurged(keys.Count > 0),
            ContinuityUnavailable unavailable => unavailable,
        };
    }

    private ArchiveManifest Stored(Ulid databaseId, ArchiveManifest manifest, string? etag, long bytes)
    {
        _cache[databaseId] = new CachedManifest(manifest, etag);
        metrics.Uploaded(ArchiveObjectKind.Manifest, bytes);
        return manifest;
    }
}

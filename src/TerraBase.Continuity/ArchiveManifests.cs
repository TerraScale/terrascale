using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging;

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

internal sealed record StagedArchiveRetirement(string Key, PendingArchiveRetirement Retirement);

internal readonly union StageArchiveRetirementOutcome(StagedArchiveRetirement, ContinuityUnavailable);

internal sealed record StagedSnapshotUpload(string Key, DateTimeOffset DeadlineUtc);

internal readonly union StageSnapshotUploadOutcome(StagedSnapshotUpload, ContinuityUnavailable);

public sealed class ArchiveManifests(S3ArchiveStore store, ContinuityMetrics metrics, TimeProvider timeProvider, ILogger<ArchiveManifests> logger)
{
    private const int MaxCompareAndSwapAttempts = 5;
    private static readonly TimeSpan RetirementSweepInterval = TimeSpan.FromHours(1);

    private readonly ConcurrentDictionary<Ulid, CachedManifest> _cache = new();
    private readonly ConcurrentDictionary<Ulid, SemaphoreSlim> _gates = new();
    private readonly ConcurrentDictionary<Ulid, DateTimeOffset> _nextRetirementSweep = new();

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

    internal ValueTask<ArchiveManifestOutcome> UpdateAsync(
        Ulid databaseId,
        Func<ArchiveManifest, ArchiveManifest> transform,
        CancellationToken cancellationToken) =>
        UpdateAsync(databaseId, current => new RetainedArchive(transform(current), []), cancellationToken);

    private async ValueTask<ArchiveManifestOutcome> UpdateAsync(
        Ulid databaseId,
        Func<ArchiveManifest, RetainedArchive> transform,
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

            current = fetched switch { CachedManifest loaded => loaded, ContinuityUnavailable => throw new UnreachableException() };
            _cache[databaseId] = current;
        }

        for (var attempt = 0; attempt < MaxCompareAndSwapAttempts; attempt++)
        {
            var transformed = transform(current.Manifest);
            if (ReferenceEquals(transformed.Manifest, current.Manifest) && transformed.RemovedKeys.Count == 0)
            {
                return current.Manifest;
            }

            StagedArchiveRetirement? retirement = null;
            if (transformed.RemovedKeys.Count > 0)
            {
                if (current.ETag is not { } expectedETag)
                {
                    return new ContinuityUnavailable("cannot retire archive objects without a manifest ETag", "archive_etag_missing");
                }

                var staged = await StageRetirementAsync(databaseId, new PendingArchiveRetirement(expectedETag, transformed.RemovedKeys), cancellationToken);
                if (staged switch { StagedArchiveRetirement => null, ContinuityUnavailable unavailable => unavailable } is { } stageError)
                {
                    return stageError;
                }

                retirement = staged switch { StagedArchiveRetirement written => written, ContinuityUnavailable => throw new UnreachableException() };
            }

            var updatedAt = timeProvider.GetUtcNow();
            if (updatedAt <= current.Manifest.UpdatedAtUtc)
            {
                updatedAt = current.Manifest.UpdatedAtUtc.AddTicks(1);
            }

            var updated = transformed.Manifest with { UpdatedAtUtc = updatedAt };
            var json = ManifestCodec.Serialize(updated);
            var stored = await store.PutBytesIfMatchAsync(ArchiveKeys.Manifest(databaseId), json, ManifestCodec.JsonContentType, current.ETag, cancellationToken);
            if (stored switch { ArchiveConditionalWritten written => written, ArchivePreconditionFailed or ArchiveUnavailable or ArchiveTimedOut => null } is { } saved)
            {
                var published = Stored(databaseId, updated, saved.ETag, json.Length);
                if (retirement is not null && await RetireAsync(databaseId, published, retirement, cancellationToken) is { } failure)
                {
                    ContinuityLog.RetirementDeferred(logger, databaseId, failure.Message);
                }

                return published;
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

            current = reloaded switch { CachedManifest fresh => fresh, ContinuityUnavailable => throw new UnreachableException() };
            _cache[databaseId] = current;
        }

        return new ContinuityUnavailable("archive manifest changed concurrently; retry", "archive_manifest_conflict");
    }

    internal async ValueTask<ArchiveManifestOutcome> PublishAsync(
        Ulid databaseId,
        IReadOnlyList<ArchivedSegment> segments,
        StagedArchiveSnapshot? snapshot,
        DateTimeOffset? cutoff,
        CancellationToken cancellationToken)
    {
        var saved = await UpdateAsync(
            databaseId,
            current =>
            {
                var retained = cutoff is { } before ? ArchiveRetention.Select(current, before) : new RetainedArchive(current, []);
                var ready = snapshot is not null && timeProvider.GetUtcNow() < snapshot.Upload.DeadlineUtc
                    ? snapshot.Snapshot
                    : null;
                return retained with { Manifest = Append(retained.Manifest, segments, ready) };
            },
            cancellationToken);
        return saved switch
        {
            ArchiveManifest published => await CompletePublicationAsync(databaseId, published, snapshot, cancellationToken),
            ContinuityUnavailable unavailable => unavailable,
        };
    }

    private async ValueTask<ArchiveManifest> CompletePublicationAsync(
        Ulid databaseId, ArchiveManifest published, StagedArchiveSnapshot? snapshot, CancellationToken cancellationToken)
    {
        if (snapshot is not null && ContainsSnapshotKey(published.Snapshots, snapshot.Snapshot.Key))
        {
            var deleted = await DeleteObjectsAsync([snapshot.Upload.Key], cancellationToken);
            ReportDeferredRetirement(databaseId, deleted);
        }

        if (await SweepRetirementsIfDueAsync(databaseId, cancellationToken) is { } failure)
        {
            ContinuityLog.RetirementDeferred(logger, databaseId, failure.Message);
        }

        return published;
    }

    private void ReportDeferredRetirement(Ulid databaseId, ArchiveObjectsDeletedOutcome deleted)
    {
        if (deleted switch { ArchiveObjectsDeleted => null, ContinuityUnavailable unavailable => unavailable } is { } failure)
        {
            ContinuityLog.RetirementDeferred(logger, databaseId, failure.Message);
        }
    }

    internal async ValueTask<ArchivePurgeOutcome> PurgeAsync(Ulid databaseId, CancellationToken cancellationToken)
    {
        _cache.TryRemove(databaseId, out _);
        _nextRetirementSweep.TryRemove(databaseId, out _);
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

        static CachedManifestOutcome Decode(Ulid id, ArchiveVersionedBytes versioned) =>
            versioned.ETag is not { Length: > 0 } etag
                ? new ContinuityUnavailable("archive manifest is missing its ETag", "archive_etag_missing")
                : ManifestCodec.Deserialize(id, versioned.Content) switch
                {
                    ArchiveManifest manifest => new CachedManifest(manifest, etag),
                    ArchiveManifestCorrupted corrupted => new ContinuityUnavailable(corrupted.Message, corrupted.Code),
                };
    }

    private static ArchiveManifest Append(ArchiveManifest current, IReadOnlyList<ArchivedSegment> segments, ArchivedSnapshot? snapshot)
    {
        if (segments.Count == 0 && snapshot is null)
        {
            return current;
        }

        var floor = ArchiveRetention.SnapshotFloor(current);
        var snapshots = snapshot is not null ? AppendSnapshot(current.Snapshots, snapshot, floor) : current.Snapshots;
        var archived = segments.Count > 0 ? AppendSegments(current.Segments, segments, floor) : current.Segments;

        return ReferenceEquals(snapshots, current.Snapshots) && ReferenceEquals(archived, current.Segments)
            ? current
            : current with { Snapshots = snapshots, Segments = archived };
    }

    private static IReadOnlyList<ArchivedSnapshot> AppendSnapshot(IReadOnlyList<ArchivedSnapshot> existing, ArchivedSnapshot snapshot, CausalBookmark floor)
    {
        var earliest = DateTimeOffset.MaxValue;
        var sorted = true;
        for (var index = 0; index < existing.Count; index++)
        {
            var item = existing[index];
            if (string.Equals(item.Key, snapshot.Key, StringComparison.Ordinal))
            {
                return existing;
            }

            if (item.TakenAtUtc < earliest)
            {
                earliest = item.TakenAtUtc;
            }

            if (index > 0 && existing[index - 1].TakenAtUtc > item.TakenAtUtc)
            {
                sorted = false;
            }
        }

        if ((existing.Count > 0 && snapshot.TakenAtUtc < earliest)
            || !CausalBookmark.TryDecode(snapshot.Bookmark, out var bookmark)
            || !bookmark.Dominates(floor))
        {
            return existing;
        }

        var next = new ArchivedSnapshot[existing.Count + 1];
        if (!sorted)
        {
            for (var index = 0; index < existing.Count; index++)
            {
                next[index] = existing[index];
            }

            next[existing.Count] = snapshot;
            Array.Sort(next, static (left, right) => left.TakenAtUtc.CompareTo(right.TakenAtUtc));
            return next;
        }

        var insertAt = existing.Count;
        while (insertAt > 0 && existing[insertAt - 1].TakenAtUtc > snapshot.TakenAtUtc)
        {
            insertAt--;
        }

        for (var index = 0; index < insertAt; index++)
        {
            next[index] = existing[index];
        }

        next[insertAt] = snapshot;
        for (var index = insertAt; index < existing.Count; index++)
        {
            next[index + 1] = existing[index];
        }

        return next;
    }

    private static IReadOnlyList<ArchivedSegment> AppendSegments(
        IReadOnlyList<ArchivedSegment> existing,
        IReadOnlyList<ArchivedSegment> segments,
        CausalBookmark floor)
    {
        if (segments.Count == 1)
        {
            var candidate = segments[0];
            if (candidate.LastSequence <= floor.Get(candidate.Origin))
            {
                return existing;
            }

            var sorted = true;
            for (var index = 0; index < existing.Count; index++)
            {
                var item = existing[index];
                if (string.Equals(item.Key, candidate.Key, StringComparison.Ordinal))
                {
                    return existing;
                }

                if (index > 0 && CompareSegment(existing[index - 1], item) > 0)
                {
                    sorted = false;
                }
            }

            var singleNext = new ArchivedSegment[existing.Count + 1];
            if (!sorted)
            {
                for (var index = 0; index < existing.Count; index++)
                {
                    singleNext[index] = existing[index];
                }

                singleNext[existing.Count] = candidate;
                Array.Sort(singleNext, CompareSegment);
                return singleNext;
            }

            var insertAt = existing.Count;
            while (insertAt > 0 && CompareSegment(existing[insertAt - 1], candidate) > 0)
            {
                insertAt--;
            }

            for (var index = 0; index < insertAt; index++)
            {
                singleNext[index] = existing[index];
            }

            singleNext[insertAt] = candidate;
            for (var index = insertAt; index < existing.Count; index++)
            {
                singleNext[index + 1] = existing[index];
            }

            return singleNext;
        }

        HashSet<string> keys = new(existing.Count + segments.Count, StringComparer.Ordinal);
        for (var index = 0; index < existing.Count; index++)
        {
            _ = keys.Add(existing[index].Key);
        }

        List<ArchivedSegment>? added = null;
        for (var index = 0; index < segments.Count; index++)
        {
            var segment = segments[index];
            if (segment.LastSequence > floor.Get(segment.Origin) && keys.Add(segment.Key))
            {
                added ??= new List<ArchivedSegment>(segments.Count - index);
                added.Add(segment);
            }
        }

        if (added is null)
        {
            return existing;
        }

        var merged = new ArchivedSegment[existing.Count + added.Count];
        for (var index = 0; index < existing.Count; index++)
        {
            merged[index] = existing[index];
        }

        for (var index = 0; index < added.Count; index++)
        {
            merged[existing.Count + index] = added[index];
        }

        Array.Sort(merged, CompareSegment);
        return merged;
    }

    private static int CompareSegment(ArchivedSegment left, ArchivedSegment right)
    {
        var origin = string.CompareOrdinal(left.Origin, right.Origin);
        return origin != 0 ? origin : left.FirstSequence.CompareTo(right.FirstSequence);
    }

    private static bool ContainsSnapshotKey(IReadOnlyList<ArchivedSnapshot> snapshots, string key)
    {
        for (var index = 0; index < snapshots.Count; index++)
        {
            if (string.Equals(snapshots[index].Key, key, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private async ValueTask<StageArchiveRetirementOutcome> StageRetirementAsync(
        Ulid databaseId,
        PendingArchiveRetirement pending,
        CancellationToken cancellationToken)
    {
        _nextRetirementSweep.TryRemove(databaseId, out _);
        var key = string.Concat(ArchiveKeys.RetirementPrefix(databaseId), Ulid.NewUlid(timeProvider.GetUtcNow()).ToString(), ".json");
        var staged = await store.PutBytesAsync(key, ManifestCodec.Serialize(pending), ManifestCodec.JsonContentType, cancellationToken);
        return staged switch
        {
            ArchiveObjectWritten => new StagedArchiveRetirement(key, pending),
            ArchiveUnavailable unavailable => new ContinuityUnavailable(unavailable.Message, unavailable.Code),
            ArchiveTimedOut timeout => new ContinuityUnavailable(timeout.Message, timeout.Code),
        };
    }

    internal async ValueTask<StageSnapshotUploadOutcome> StageSnapshotUploadAsync(Ulid databaseId, string snapshotKey, CancellationToken cancellationToken)
    {
        var current = await FetchRemoteAsync(databaseId, cancellationToken);
        if (current switch { CachedManifest => null, ContinuityUnavailable unavailable => unavailable } is { } loadError)
        {
            return loadError;
        }

        var etag = current switch { CachedManifest cached => cached.ETag, ContinuityUnavailable => throw new UnreachableException() };
        var deadline = timeProvider.GetUtcNow() + 2 * S3ArchiveStore.RequestTimeout;
        var pending = new PendingArchiveRetirement(etag, [snapshotKey]) { SnapshotUploadDeadlineUtc = deadline };
        var staged = await StageRetirementAsync(databaseId, pending, cancellationToken);
        return staged switch
        {
            StagedArchiveRetirement retirement => new StagedSnapshotUpload(retirement.Key, deadline),
            ContinuityUnavailable unavailable => unavailable,
        };
    }

    private async ValueTask<ContinuityUnavailable?> RetireAsync(
        Ulid databaseId,
        ArchiveManifest manifest,
        StagedArchiveRetirement staged,
        CancellationToken cancellationToken)
    {
        var referenced = manifest.Snapshots.Select(static snapshot => snapshot.Key)
            .Concat(manifest.Segments.Select(static segment => segment.Key))
            .ToHashSet(StringComparer.Ordinal);
        var prefix = ArchiveKeys.DatabasePrefix(databaseId);
        var segmentsPrefix = string.Concat(prefix, "segments/");
        var snapshotsPrefix = string.Concat(prefix, "snapshots/");
        var pending = staged.Retirement.Keys.Where(key => key is not null && !referenced.Contains(key) && IsSafeArchiveKey(key)
            && (key.StartsWith(segmentsPrefix, StringComparison.Ordinal) || key.StartsWith(snapshotsPrefix, StringComparison.Ordinal)))
            .Distinct(StringComparer.Ordinal)
            .Append(staged.Key);
        var deleted = await DeleteObjectsAsync(pending, cancellationToken);
        return deleted switch
        {
            ArchiveObjectsDeleted => null,
            ContinuityUnavailable unavailable => unavailable,
        };
    }

    private async ValueTask<ContinuityUnavailable?> SweepRetirementsIfDueAsync(Ulid databaseId, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        if (_nextRetirementSweep.TryGetValue(databaseId, out var next) && now < next)
        {
            return null;
        }

        var listed = await store.ListKeysAsync(ArchiveKeys.RetirementPrefix(databaseId), cancellationToken);
        var failure = listed switch
        {
            IReadOnlyList<string> { Count: 0 } => null,
            IReadOnlyList<string> keys => await SweepRetirementsAsync(databaseId, keys, cancellationToken),
            ArchiveUnavailable unavailable => new ContinuityUnavailable(unavailable.Message, unavailable.Code),
            ArchiveTimedOut timeout => new ContinuityUnavailable(timeout.Message, timeout.Code),
        };
        if (failure is null)
        {
            _nextRetirementSweep[databaseId] = now + RetirementSweepInterval;
        }

        return failure;
    }

    private async ValueTask<ContinuityUnavailable?> SweepRetirementsAsync(
        Ulid databaseId,
        IReadOnlyList<string> keys,
        CancellationToken cancellationToken)
    {
        var sweepStarted = timeProvider.GetUtcNow();
        var fetched = await FetchRemoteAsync(databaseId, cancellationToken);
        if (fetched switch { CachedManifest => null, ContinuityUnavailable unavailable => unavailable } is { } loadError)
        {
            return loadError;
        }

        var current = fetched switch { CachedManifest manifest => manifest, ContinuityUnavailable => throw new UnreachableException() };
        var prefix = ArchiveKeys.RetirementPrefix(databaseId);
        foreach (var key in keys.Where(key => key.StartsWith(prefix, StringComparison.Ordinal) && IsSafeArchiveKey(key)))
        {
            var marker = await store.GetBytesAsync(key, cancellationToken);
            var failure = marker switch
            {
                byte[] json => await SweepRetirementAsync(databaseId, current, key, json, sweepStarted, cancellationToken),
                ArchiveObjectMissing => null,
                ArchiveUnavailable unavailable => new ContinuityUnavailable(unavailable.Message, unavailable.Code),
                ArchiveTimedOut timeout => new ContinuityUnavailable(timeout.Message, timeout.Code),
            };
            if (failure is not null)
            {
                return failure;
            }
        }

        return null;
    }

    private async ValueTask<ContinuityUnavailable?> SweepRetirementAsync(
        Ulid databaseId,
        CachedManifest current,
        string key,
        ReadOnlyMemory<byte> json,
        DateTimeOffset sweepStarted,
        CancellationToken cancellationToken)
    {
        var decoded = ManifestCodec.DeserializeRetirement(json.Span);
        return decoded switch
        {
            PendingArchiveRetirement { SnapshotUploadDeadlineUtc: { } deadline } when sweepStarted - S3ArchiveStore.RequestTimeout < deadline => null,
            PendingArchiveRetirement { SnapshotUploadDeadlineUtc: not null } pending =>
                await SweepSnapshotUploadAsync(databaseId, current.Manifest, new StagedArchiveRetirement(key, pending), cancellationToken),
            PendingArchiveRetirement pending when string.Equals(pending.ManifestETag, current.ETag, StringComparison.Ordinal) => null,
            PendingArchiveRetirement pending => await RetireAsync(databaseId, current.Manifest, new StagedArchiveRetirement(key, pending), cancellationToken),
            ArchiveRetirementCorrupted corrupted => await DeleteCorruptRetirementAsync(databaseId, key, corrupted, cancellationToken),
        };
    }

    private async ValueTask<ContinuityUnavailable?> SweepSnapshotUploadAsync(
        Ulid databaseId, ArchiveManifest current, StagedArchiveRetirement upload, CancellationToken cancellationToken)
    {
        if (ContainsSnapshotKey(current.Snapshots, upload.Retirement.Keys[0]))
        {
            var deleted = await DeleteObjectsAsync([upload.Key], cancellationToken);
            return deleted switch
            {
                ArchiveObjectsDeleted => null,
                ContinuityUnavailable unavailable => unavailable,
            };
        }

        var fenced = await UpdateAsync(databaseId, static manifest => manifest with { }, cancellationToken);
        return fenced switch
        {
            ArchiveManifest published => await RetireAsync(databaseId, published, upload, cancellationToken),
            ContinuityUnavailable unavailable => unavailable,
        };
    }

    private async ValueTask<ContinuityUnavailable?> DeleteCorruptRetirementAsync(
        Ulid databaseId, string key, ArchiveRetirementCorrupted corrupted, CancellationToken cancellationToken)
    {
        ContinuityLog.RetirementCorrupted(logger, key, corrupted.Message);
        var deleted = await DeleteObjectsAsync([key], cancellationToken);
        ReportDeferredRetirement(databaseId, deleted);
        return null;
    }

    private static bool IsSafeArchiveKey(string key) => !key.Contains("..", StringComparison.Ordinal)
        && key.All(static character => char.IsAsciiLetterOrDigit(character) || character is '/' or '-' or '_' or '.');

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

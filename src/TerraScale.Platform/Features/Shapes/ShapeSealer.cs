using System.Collections.Concurrent;
using System.Globalization;

namespace TerraScale.Platform.Features.Shapes;

public readonly union SyncShapeOutcome(ShapeRecord, PlatformUnavailable);

public readonly union LoadShapeChunkOutcome(ShapeChunk, PlatformNotFound, PlatformUnavailable);

public sealed record ShapeDrain(int Due, string? Deferred);

public sealed class ShapeSealer(
    ShapeRegistry registry,
    ShapeObjectStore objects,
    ShapeCrypto crypto,
    ShapeCdn cdn,
    ShapeNotifier notifier,
    PlatformMetrics metrics)
{
    private const int CacheLimit = 512;
    private const long CacheBytes = 64L * 1024 * 1024;
    private const string ImmutableCache = "public, max-age=31536000, immutable";
    private const int DrainPage = 64;
    private readonly ConcurrentDictionary<string, CachedChunk> _chunks = new(StringComparer.Ordinal);
    private readonly Lock _cacheGate = new();
    private long _cachedBytes;
    private string _drained = "";

    public async ValueTask<SyncShapeOutcome> SyncAsync(ShapeRecord shape, ShapeSnapshot snapshot, CancellationToken cancellationToken)
    {
        if (snapshot.Digest == shape.HeadDigest)
        {
            return shape;
        }

        if (!objects.IsConfigured)
        {
            return new PlatformUnavailable("shape object storage is not configured", "shapes_unconfigured");
        }

        if (shape.Head == 0)
        {
            return await SealFromAsync([]);
        }

        var outcome = await LoadAsync(shape.Handle, shape.Head, cancellationToken);
        return outcome switch
        {
            ShapeChunk chunk => await SealFromAsync(chunk.Rows),
            PlatformNotFound => await SealFromAsync([]),
            PlatformUnavailable unavailable => unavailable,
        };

        ValueTask<SyncShapeOutcome> SealFromAsync(IReadOnlyList<ShapeRowEntry> rows) =>
            SealAsync(shape, snapshot, Diff(rows, snapshot.Rows, shape.Where is not null), cancellationToken);
    }

    public async ValueTask<LoadShapeChunkOutcome> LoadAsync(string handle, long offset, CancellationToken cancellationToken)
    {
        var cacheKey = handle + ":" + offset.ToString(CultureInfo.InvariantCulture);
        if (_chunks.TryGetValue(cacheKey, out var cached))
        {
            return cached.Chunk;
        }

        var outcome = await registry.ChunkAsync(handle, offset, cancellationToken);
        return outcome switch
        {
            ShapeChunkInfo info => await LoadObjectAsync(info),
            PlatformNotFound missing => missing,
            PlatformUnavailable unavailable => unavailable,
        };

        async ValueTask<LoadShapeChunkOutcome> LoadObjectAsync(ShapeChunkInfo info)
        {
            var outcome = await objects.GetAsync(ShapeObjectStore.Key(handle, info.Name), cancellationToken);
            return outcome switch
            {
                byte[] bytes => crypto.Open(handle, bytes) switch
                {
                    OpenedShapeChunk opened => Remember(cacheKey, opened.Chunk, opened.PlainBytes),
                    PlatformUnavailable unavailable => unavailable,
                },
                PlatformNotFound missing => missing,
                PlatformUnavailable unavailable => unavailable,
            };
        }
    }

    private async ValueTask<SyncShapeOutcome> SealAsync(
        ShapeRecord shape,
        ShapeSnapshot snapshot,
        IReadOnlyList<ShapeOperation> ops,
        CancellationToken cancellationToken)
    {
        var offset = shape.Head + 1;
        var chunk = new ShapeChunk(offset, shape.Head, snapshot.Digest, snapshot.Columns, snapshot.Rows, ops);
        var sealedChunk = crypto.Seal(shape.Handle, shape.Database, chunk);
        var sealer = Ulid.NewUlid().ToString();
        var name = ShapeObjectStore.ObjectName(offset, snapshot.Digest, sealer);
        var info = new ShapeChunkInfo(offset, name, sealedChunk.Sha256, sealedChunk.Bytes.LongLength, sealedChunk.Epoch, snapshot.Digest);
        var guarded = await registry.GuardObjectAsync(shape.Handle, name, sealer, cancellationToken);
        return guarded switch
        {
            ShapeDeletionsUpdated => await StoreAsync(),
            PlatformUnavailable unavailable => unavailable,
        };

        async ValueTask<SyncShapeOutcome> StoreAsync()
        {
            var stored = await objects.PutAsync(ShapeObjectStore.Key(shape.Handle, name), sealedChunk.Bytes, "application/octet-stream", ImmutableCache, cancellationToken);
            return stored switch
            {
                ShapeObjectStored => await CommitAsync(),
                PlatformUnavailable unavailable => unavailable,
            };
        }

        async ValueTask<SyncShapeOutcome> CommitAsync()
        {
            var outcome = await registry.CommitAsync(shape, info, snapshot.Rows.Count, sealer, cancellationToken);
            return outcome switch
            {
                true => await PublishAsync(shape with { Head = offset, HeadDigest = snapshot.Digest }, chunk, sealedChunk.PlainBytes, cancellationToken),
                false => await AbandonAsync(),
                PlatformUnavailable unavailable => unavailable,
            };
        }

        async ValueTask<SyncShapeOutcome> AbandonAsync()
        {
            var requeued = await registry.GuardObjectAsync(shape.Handle, name, Ulid.NewUlid().ToString(), cancellationToken);
            return requeued switch
            {
                ShapeDeletionsUpdated => await RereadAsync(shape, cancellationToken),
                PlatformUnavailable unavailable => unavailable,
            };
        }
    }

    private async ValueTask<SyncShapeOutcome> PublishAsync(ShapeRecord shape, ShapeChunk chunk, long plainBytes, CancellationToken cancellationToken)
    {
        metrics.ShapeChunk();
        _ = Remember(shape.Handle + ":" + chunk.Offset.ToString(CultureInfo.InvariantCulture), chunk, plainBytes);
        var trimmedOutcome = await registry.TrimAsync(shape.Handle, chunk.Offset, cancellationToken);
        var trimmed = trimmedOutcome switch
        {
            ShapeChunks { Items: var retired } => retired,
            PlatformUnavailable => [],
        };
        foreach (var retired in trimmed)
        {
            Forget(shape.Handle + ":" + retired.Offset.ToString(CultureInfo.InvariantCulture));
        }

        notifier.Signal(shape.Database);
        return shape;
    }

    public void ForgetShapes(IReadOnlyList<string> handles)
    {
        ArgumentNullException.ThrowIfNull(handles);
        foreach (var handle in handles)
        {
            foreach (var key in _chunks.Keys.Where(key => key.StartsWith(handle + ":", StringComparison.Ordinal)))
            {
                Forget(key);
            }
        }
    }

    public async ValueTask<ShapeDrain> DrainAsync(CancellationToken cancellationToken)
    {
        var dueOutcome = await registry.DueDeletionsAsync(_drained, DrainPage, cancellationToken);
        return dueOutcome switch
        {
            ShapeObjectDeletions { Items: var due } => new ShapeDrain(due.Count, await ClaimAsync(due)),
            PlatformUnavailable unavailable => new ShapeDrain(0, unavailable.Message),
        };

        async ValueTask<string?> ClaimAsync(IReadOnlyList<ShapeObjectDeletion> due)
        {
            _drained = due.Count < DrainPage ? "" : due[^1].Id;
            var claimedOutcome = await registry.ClaimDeletionsAsync(due, cancellationToken);
            return claimedOutcome switch
            {
                ShapeObjectDeletions { Items: var claimed } => await EraseAsync(claimed),
                PlatformUnavailable unavailable => unavailable.Message,
            };
        }

        async ValueTask<string?> EraseAsync(IReadOnlyList<ShapeObjectDeletion> claimed)
        {
            var done = new List<string>(claimed.Count);
            var purge = new List<string>();
            foreach (var item in claimed)
            {
                var deletedOutcome = await objects.DeleteAsync(ShapeObjectStore.Key(item.Handle, item.Name), cancellationToken);
                var deleted = deletedOutcome switch
                {
                    ShapeObjectDeleted => true,
                    PlatformUnavailable => false,
                };
                if (deleted)
                {
                    done.Add(item.Id);
                    purge.Add(objects.Url(item.Handle, item.Name));
                }
            }

            await cdn.QueueAsync(purge, cancellationToken);
            var cleared = await registry.ClearDeletionsAsync(done, cancellationToken);
            return cleared switch
            {
                ShapeDeletionsUpdated => done.Count == claimed.Count ? null : $"{claimed.Count - done.Count} shape objects could not be deleted and stay queued",
                PlatformUnavailable unavailable => unavailable.Message,
            };
        }
    }

    private async ValueTask<SyncShapeOutcome> RereadAsync(ShapeRecord shape, CancellationToken cancellationToken)
    {
        var currentOutcome = await registry.ReadAsync(shape.Handle, cancellationToken);
        return currentOutcome switch
        {
            ShapeRecord record => record,
            PlatformNotFound missing => new PlatformUnavailable(missing.Message ?? "the shape vanished while sealing", "shape_not_found"),
            PlatformUnavailable unavailable => unavailable,
        };
    }

    private ShapeChunk Remember(string key, ShapeChunk chunk, long bytes)
    {
        if (bytes > CacheBytes / 4)
        {
            return chunk;
        }

        lock (_cacheGate)
        {
            if (_chunks.TryRemove(key, out var replaced))
            {
                _cachedBytes -= replaced.Bytes;
            }

            if (_chunks.Count >= CacheLimit || _cachedBytes + bytes > CacheBytes)
            {
                _chunks.Clear();
                _cachedBytes = 0;
            }

            _chunks[key] = new CachedChunk(chunk, bytes);
            _cachedBytes += bytes;
        }

        return chunk;
    }

    private void Forget(string key)
    {
        lock (_cacheGate)
        {
            if (_chunks.TryRemove(key, out var removed))
            {
                _cachedBytes -= removed.Bytes;
            }
        }
    }

    private sealed record CachedChunk(ShapeChunk Chunk, long Bytes);

    public static IReadOnlyList<ShapeOperation> Diff(IReadOnlyList<ShapeRowEntry> before, IReadOnlyList<ShapeRowEntry> after, bool filtered)
    {
        var previous = before.ToDictionary(static row => row.Key, static row => row.Row.GetRawText(), StringComparer.Ordinal);
        var current = after.Select(static row => row.Key).ToHashSet(StringComparer.Ordinal);
        var ops = new List<ShapeOperation>();
        foreach (var row in after)
        {
            if (!previous.TryGetValue(row.Key, out var raw))
            {
                ops.Add(new ShapeOperation("insert", row.Key, row.Row));
            }
            else if (!string.Equals(raw, row.Row.GetRawText(), StringComparison.Ordinal))
            {
                ops.Add(new ShapeOperation("update", row.Key, row.Row));
            }
        }

        foreach (var row in before.Where(row => !current.Contains(row.Key)))
        {
            ops.Add(new ShapeOperation("delete", row.Key, null, filtered ? "out" : null));
        }

        return [.. ops.OrderBy(static op => long.TryParse(op.Key, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var key) ? key : 0)];
    }
}

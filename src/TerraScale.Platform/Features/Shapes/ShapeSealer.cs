using System.Collections.Concurrent;
using System.Globalization;

namespace TerraScale.Platform.Features.Shapes;

public readonly union SyncShapeOutcome(ShapeRecord, PlatformUnavailable);

public readonly union LoadShapeChunkOutcome(ShapeChunk, PlatformNotFound, PlatformUnavailable);

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
    private readonly ConcurrentDictionary<string, CachedChunk> _chunks = new(StringComparer.Ordinal);
    private readonly Lock _cacheGate = new();
    private long _cachedBytes;

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
        var name = ShapeObjectStore.ObjectName(offset, snapshot.Digest);
        var info = new ShapeChunkInfo(offset, name, sealedChunk.Sha256, sealedChunk.Bytes.LongLength, sealedChunk.Epoch, snapshot.Digest);
        var stored = await objects.PutAsync(ShapeObjectStore.Key(shape.Handle, name), sealedChunk.Bytes, "application/octet-stream", ImmutableCache, cancellationToken);
        return stored switch
        {
            ShapeObjectStored => await CommitAsync(),
            PlatformUnavailable unavailable => unavailable,
        };

        async ValueTask<SyncShapeOutcome> CommitAsync()
        {
            var outcome = await registry.CommitAsync(shape, info, snapshot.Rows.Count, cancellationToken);
            return outcome switch
            {
                true => await PublishAsync(shape with { Head = offset, HeadDigest = snapshot.Digest }, chunk, sealedChunk.PlainBytes, cancellationToken),
                false => await RereadAsync(shape, name, cancellationToken),
                PlatformUnavailable unavailable => unavailable,
            };
        }
    }

    private async ValueTask<SyncShapeOutcome> PublishAsync(ShapeRecord shape, ShapeChunk chunk, long plainBytes, CancellationToken cancellationToken)
    {
        metrics.ShapeChunk();
        _ = Remember(shape.Handle + ":" + chunk.Offset.ToString(CultureInfo.InvariantCulture), chunk, plainBytes);
        var pointerName = "head.json";
        var pointer = JsonSerializer.SerializeToUtf8Bytes(
            new ShapeHeadPointer(shape.Handle, chunk.Offset.ToString(CultureInfo.InvariantCulture), ShapeObjectStore.ObjectName(chunk.Offset, chunk.Digest)),
            ShapesJsonContext.Default.ShapeHeadPointer);
        var purge = new List<string> { objects.Url(shape.Handle, pointerName) };
        var published = await objects.PutAsync(ShapeObjectStore.Key(shape.Handle, pointerName), pointer, "application/json", "public, max-age=1", cancellationToken);
        var trimmedOutcome = await registry.TrimAsync(shape.Handle, chunk.Offset, cancellationToken);
        var trimmed = trimmedOutcome switch
        {
            ShapeChunks { Items: var retired } => retired,
            PlatformUnavailable => [],
        };
        foreach (var retired in trimmed)
        {
            Forget(shape.Handle + ":" + retired.Offset.ToString(CultureInfo.InvariantCulture));
            var erasedOutcome = await objects.DeleteAsync(ShapeObjectStore.Key(shape.Handle, retired.Name), cancellationToken);
            var erased = erasedOutcome switch
            {
                ShapeObjectDeleted => true,
                PlatformUnavailable => false,
            };
            if (erased)
            {
                purge.Add(objects.Url(shape.Handle, retired.Name));
            }
        }

        await cdn.QueueAsync(purge, cancellationToken);
        notifier.Signal(shape.Database);
        return published switch
        {
            ShapeObjectStored => shape,
            PlatformUnavailable unavailable => unavailable,
        };
    }

    public async ValueTask<int> RetireAsync(ShapeRetirement retirement, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(retirement);
        foreach (var handle in retirement.Handles)
        {
            foreach (var key in _chunks.Keys.Where(key => key.StartsWith(handle + ":", StringComparison.Ordinal)))
            {
                Forget(key);
            }
        }

        var purge = new List<string>();
        var kept = 0;
        foreach (var chunk in retirement.Chunks.Concat(retirement.Handles.Select(static handle => new ShapeChunkObject(handle, "head.json"))))
        {
            var deletedOutcome = await objects.DeleteAsync(ShapeObjectStore.Key(chunk.Handle, chunk.Name), cancellationToken);
            var deleted = deletedOutcome switch
            {
                ShapeObjectDeleted => true,
                PlatformUnavailable => false,
            };
            if (deleted)
            {
                purge.Add(objects.Url(chunk.Handle, chunk.Name));
            }
            else
            {
                kept++;
            }
        }

        await cdn.QueueAsync(purge, cancellationToken);
        return kept;
    }

    private async ValueTask<SyncShapeOutcome> RereadAsync(ShapeRecord shape, string lostObject, CancellationToken cancellationToken)
    {
        var currentOutcome = await registry.ReadAsync(shape.Handle, cancellationToken);
        SyncShapeOutcome current = currentOutcome switch
        {
            ShapeRecord record => record,
            PlatformNotFound missing => new PlatformUnavailable(missing.Message ?? "the shape vanished while sealing", "shape_not_found"),
            PlatformUnavailable unavailable => unavailable,
        };
        var orphaned = current switch
        {
            ShapeRecord record => record.HeadDigest.Length < 16
                || !string.Equals(ShapeObjectStore.ObjectName(record.Head, record.HeadDigest), lostObject, StringComparison.Ordinal),
            PlatformUnavailable => false,
        };
        if (!orphaned)
        {
            return current;
        }

        var outcome = await objects.DeleteAsync(ShapeObjectStore.Key(shape.Handle, lostObject), cancellationToken);
        return outcome switch
        {
            ShapeObjectDeleted => current,
            PlatformUnavailable => current,
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

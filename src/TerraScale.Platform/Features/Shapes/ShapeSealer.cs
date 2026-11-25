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
        var name = ShapeObjectStore.ObjectName(offset, snapshot.Digest);
        var info = new ShapeChunkInfo(offset, name, sealedChunk.Sha256, sealedChunk.Bytes.LongLength, sealedChunk.Epoch, snapshot.Digest);
        var sealer = Ulid.NewUlid().ToString();
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
                true => await PublishAsync(shape with { Head = offset, HeadDigest = snapshot.Digest }, chunk, sealedChunk.PlainBytes, sealer, cancellationToken),
                false => await RereadAsync(shape, cancellationToken),
                PlatformUnavailable unavailable => unavailable,
            };
        }
    }

    private async ValueTask<SyncShapeOutcome> PublishAsync(ShapeRecord shape, ShapeChunk chunk, long plainBytes, string sealer, CancellationToken cancellationToken)
    {
        metrics.ShapeChunk();
        _ = Remember(shape.Handle + ":" + chunk.Offset.ToString(CultureInfo.InvariantCulture), chunk, plainBytes);
        var pointerName = ShapeRegistry.HeadPointer;
        var pointer = JsonSerializer.SerializeToUtf8Bytes(
            new ShapeHeadPointer(shape.Handle, chunk.Offset.ToString(CultureInfo.InvariantCulture), ShapeObjectStore.ObjectName(chunk.Offset, chunk.Digest)),
            ShapesJsonContext.Default.ShapeHeadPointer);
        var purge = new List<string> { objects.Url(shape.Handle, pointerName) };
        var published = await objects.PutAsync(ShapeObjectStore.Key(shape.Handle, pointerName), pointer, "application/json", "public, max-age=1", cancellationToken);
        var trimmedOutcome = await registry.TrimAsync(shape.Handle, chunk.Offset, sealer, cancellationToken);
        var trimmed = trimmedOutcome switch
        {
            ShapeChunks { Items: var retired } => retired,
            PlatformUnavailable => [],
        };
        foreach (var retired in trimmed)
        {
            Forget(shape.Handle + ":" + retired.Offset.ToString(CultureInfo.InvariantCulture));
        }

        await cdn.QueueAsync(purge, cancellationToken);
        notifier.Signal(shape.Database);
        return published switch
        {
            ShapeObjectStored => shape,
            PlatformUnavailable unavailable => unavailable,
        };
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

    public async ValueTask<string?> DrainAsync(CancellationToken cancellationToken)
    {
        var outcome = await registry.DueDeletionsAsync(_drained, DrainPage, cancellationToken);
        return outcome switch
        {
            ShapeObjectDeletions { Items: var items } => await EraseAsync(items),
            PlatformUnavailable unavailable => unavailable.Message,
        };

        async ValueTask<string?> EraseAsync(IReadOnlyList<ShapeObjectDeletion> items)
        {
            _drained = items.Count < DrainPage ? "" : items[^1].Id;
            var done = new List<string>(items.Count);
            var purge = new List<string>();
            foreach (var item in items)
            {
                if (item.Referenced)
                {
                    done.Add(item.Id);
                    continue;
                }

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
                ShapeDeletionsUpdated => done.Count == items.Count ? null : $"{items.Count - done.Count} shape objects could not be deleted and stay queued",
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

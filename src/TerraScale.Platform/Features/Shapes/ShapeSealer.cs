using System.Collections.Concurrent;
using System.Globalization;

namespace TerraScale.Platform.Features.Shapes;

public sealed class ShapeSealer(
    ShapeRegistry registry,
    ShapeObjectStore objects,
    ShapeCrypto crypto,
    ShapeCdn cdn,
    ShapeNotifier notifier,
    PlatformMetrics metrics)
{
    private const int CacheLimit = 512;
    private const string ImmutableCache = "public, max-age=31536000, immutable";
    private readonly ConcurrentDictionary<string, ShapeChunk> _chunks = new(StringComparer.Ordinal);

    public async ValueTask<Result<ShapeRecord, Unavailable>> SyncAsync(ShapeRecord shape, ShapeSnapshot snapshot, CancellationToken cancellationToken)
    {
        if (snapshot.Digest == shape.HeadDigest)
        {
            return shape;
        }

        if (!objects.IsConfigured)
        {
            return new Unavailable("shape object storage is not configured", "shapes_unconfigured");
        }

        var previous = shape.Head == 0
            ? Rows([])
            : await LoadAsync(shape.Handle, shape.Head, cancellationToken) switch
            {
                ShapeChunk chunk => Rows(chunk.Rows),
                NotFound => Rows([]),
                Unavailable unavailable => unavailable,
            };
        return previous switch
        {
            IReadOnlyList<ShapeRowEntry> rows => await SealAsync(shape, snapshot, Diff(rows, snapshot.Rows, shape.Where is not null), cancellationToken),
            Unavailable unavailable => unavailable,
        };
    }

    public async ValueTask<Result<ShapeChunk, NotFound, Unavailable>> LoadAsync(string handle, long offset, CancellationToken cancellationToken)
    {
        var cacheKey = handle + ":" + offset.ToString(CultureInfo.InvariantCulture);
        if (_chunks.TryGetValue(cacheKey, out var cached))
        {
            return cached;
        }

        return await registry.ChunkAsync(handle, offset, cancellationToken) switch
        {
            ShapeChunkInfo info => await objects.GetAsync(ShapeObjectStore.Key(handle, info.Name), cancellationToken) switch
            {
                byte[] bytes => crypto.Open(handle, bytes) switch
                {
                    ShapeChunk chunk => Remember(cacheKey, chunk),
                    Unavailable unavailable => unavailable,
                },
                NotFound missing => missing,
                Unavailable unavailable => unavailable,
            },
            NotFound missing => missing,
            Unavailable unavailable => unavailable,
        };
    }

    private async ValueTask<Result<ShapeRecord, Unavailable>> SealAsync(
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
            Done => await registry.CommitAsync(shape, info, snapshot.Rows.Count, cancellationToken) switch
            {
                true => await PublishAsync(shape with { Head = offset, HeadDigest = snapshot.Digest }, chunk, cancellationToken),
                false => await RereadAsync(shape, name, cancellationToken),
                Unavailable unavailable => unavailable,
            },
            Unavailable unavailable => unavailable,
        };
    }

    private async ValueTask<Result<ShapeRecord, Unavailable>> PublishAsync(ShapeRecord shape, ShapeChunk chunk, CancellationToken cancellationToken)
    {
        metrics.ShapeChunk();
        Remember(shape.Handle + ":" + chunk.Offset.ToString(CultureInfo.InvariantCulture), chunk);
        var pointerName = "head.json";
        var pointer = JsonSerializer.SerializeToUtf8Bytes(
            new ShapeHeadPointer(shape.Handle, chunk.Offset.ToString(CultureInfo.InvariantCulture), ShapeObjectStore.ObjectName(chunk.Offset, chunk.Digest)),
            ShapesJsonContext.Default.ShapeHeadPointer);
        var purge = new List<string> { objects.Url(shape.Handle, pointerName) };
        var published = await objects.PutAsync(ShapeObjectStore.Key(shape.Handle, pointerName), pointer, "application/json", "public, max-age=1", cancellationToken);
        var trimmed = await registry.TrimAsync(shape.Handle, chunk.Offset, cancellationToken) switch
        {
            IReadOnlyList<ShapeChunkInfo> retired => retired,
            Unavailable => [],
        };
        foreach (var retired in trimmed)
        {
            _ = _chunks.TryRemove(shape.Handle + ":" + retired.Offset.ToString(CultureInfo.InvariantCulture), out _);
            var erased = await objects.DeleteAsync(ShapeObjectStore.Key(shape.Handle, retired.Name), cancellationToken) switch
            {
                Done => true,
                Unavailable => false,
            };
            if (erased)
            {
                purge.Add(objects.Url(shape.Handle, retired.Name));
            }
        }

        var purged = await cdn.PurgeAsync(purge, cancellationToken);
        notifier.Signal(shape.Database);
        return published switch
        {
            Done => purged switch
            {
                Done => shape,
                Unavailable unavailable => unavailable,
            },
            Unavailable unavailable => unavailable,
        };
    }

    private async ValueTask<Result<ShapeRecord, Unavailable>> RereadAsync(ShapeRecord shape, string lostObject, CancellationToken cancellationToken)
    {
        var current = await registry.ReadAsync(shape.Handle, cancellationToken) switch
        {
            ShapeRecord record => Winner(record),
            NotFound missing => new Unavailable(missing.Message ?? "the shape vanished while sealing", "shape_not_found"),
            Unavailable unavailable => unavailable,
        };
        var orphaned = current switch
        {
            ShapeRecord record => record.HeadDigest.Length < 16
                || !string.Equals(ShapeObjectStore.ObjectName(record.Head, record.HeadDigest), lostObject, StringComparison.Ordinal),
            Unavailable => false,
        };
        if (!orphaned)
        {
            return current;
        }

        return await objects.DeleteAsync(ShapeObjectStore.Key(shape.Handle, lostObject), cancellationToken) switch
        {
            Done => current,
            Unavailable => current,
        };
    }

    private static Result<ShapeRecord, Unavailable> Winner(ShapeRecord record) => record;

    private ShapeChunk Remember(string key, ShapeChunk chunk)
    {
        if (_chunks.Count >= CacheLimit)
        {
            _chunks.Clear();
        }

        _chunks[key] = chunk;
        return chunk;
    }

    private static Result<IReadOnlyList<ShapeRowEntry>, Unavailable> Rows(IReadOnlyList<ShapeRowEntry> rows) => new(rows);

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

using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using TerraBase.Engine;
using TerraBase.Features.Memory;
using ZLogger;

namespace TerraBase.Features.Shapes;

internal sealed record ShapeSnapshot(long Offset, long SealedAt)
{
    public string FirstOffset => ShapeSnapshots.PageOffset(Offset, 0);
}

internal sealed class ShapeSnapshotPage(ReadOnlyMemory<byte> body, long? nextRowid, IMemoryReservation reservation) : IDisposable
{
    public ReadOnlyMemory<byte> Body { get; } = body;

    public long? NextRowid { get; } = nextRowid;

    public void Dispose() => reservation.Dispose();
}

internal interface IShapeSnapshotSealer
{
    ValueTask<ShapeSnapshot?> NewestAsync(IDatabaseStore store, string handle, CancellationToken cancellationToken);

    ValueTask<ShapeSnapshot> SealIfDueAsync(IDatabaseStore store, ShapeSealDefinition shape, CancellationToken cancellationToken);

    ValueTask<ShapeSnapshotPage?> ReadPageAsync(IDatabaseStore store, string handle, long offset, int page, CancellationToken cancellationToken);
}

internal interface IShapeSnapshotStorage
{
    ValueTask<ShapeSnapshot?> NewestShapeSnapshotAsync(string handle, CancellationToken cancellationToken);

    ValueTask<bool> ShapeSnapshotDueAsync(string handle, long now, CancellationToken cancellationToken);

    ValueTask<long> ShapeSnapshotHeadAsync(CancellationToken cancellationToken);

    ValueTask<ShapeSnapshotPage> BuildShapeSnapshotPageAsync(ShapeSealDefinition shape, long offset, int page, long? afterRowid, IMemoryBudget memory, TimeProvider time, CancellationToken cancellationToken);

    ValueTask InsertShapeSnapshotPageAsync(ShapeSealDefinition shape, long offset, int page, ShapeSnapshotPage body, long sealedAt, CancellationToken cancellationToken);

    ValueTask PrepareShapeSnapshotAsync(ShapeSealDefinition shape, long offset, CancellationToken cancellationToken);

    ValueTask<ShapeSnapshotPage?> ReadShapeSnapshotPageAsync(string handle, long offset, int page, IMemoryBudget memory, CancellationToken cancellationToken);
}

internal interface IShapeSnapshotTransactions
{
    ValueTask<IStorageTransaction> BeginAsync(IDatabaseStore store, bool write, CancellationToken cancellationToken);
}

internal sealed class ShapeSnapshots(
    IMemoryBudget memory, TimeProvider time, IShapeSnapshotTransactions transactions, IShapeMetrics? metrics = null,
    Action<IDatabaseStore, ShapeSealDefinition, ShapeSnapshot>? onSealed = null, ILogger<ShapeSnapshots>? logger = null)
    : IShapeSnapshotSealer
{
    private int _sealing;
    internal const int PageRows = 1000;
    internal const int PageBytes = 1024 * 1024;
    internal const int SingletonBytes = 8 * PageBytes;
    internal const int WorkspaceBytes = PageBytes + 64 * 1024;
    internal const int ResealChunks = 4096;
    internal const int ResealChunksPerPage = 16;
    internal static readonly TimeSpan ResealAge = TimeSpan.FromHours(1);
    internal static readonly TimeSpan PageDeadline = TimeSpan.FromSeconds(5);

    public async ValueTask<ShapeSnapshot?> NewestAsync(IDatabaseStore store, string handle, CancellationToken cancellationToken)
    {
        using var admission = memory.TryReserve(4096, store.Tenant) ?? throw new StorageBusyException();
        await using var transaction = await transactions.BeginAsync(store, write: false, cancellationToken).ConfigureAwait(false);
        var newest = await Storage(transaction).NewestShapeSnapshotAsync(handle, cancellationToken).ConfigureAwait(false);
        return newest is not null && newest.Offset <= await FrontierAsync(transaction, cancellationToken).ConfigureAwait(false) ? newest : null;
    }

    public async ValueTask<ShapeSnapshot> SealIfDueAsync(IDatabaseStore store, ShapeSealDefinition shape, CancellationToken cancellationToken)
    {
        ShapeTables.Validate(shape);
        using (var admission = memory.TryReserve(4096, store.Tenant) ?? throw new StorageBusyException())
        await using (var probe = await transactions.BeginAsync(store, write: false, cancellationToken).ConfigureAwait(false))
        {
            if (!await Storage(probe).ShapeSnapshotDueAsync(shape.Handle, time.GetUtcNow().ToUnixTimeMilliseconds(), cancellationToken).ConfigureAwait(false))
            {
                return await PublishedSnapshotAsync(probe, shape.Handle, cancellationToken).ConfigureAwait(false);
            }
        }

        if (Interlocked.CompareExchange(ref _sealing, 1, 0) != 0)
        {
            throw new StorageBusyException();
        }

        try
        {
            using var admission = memory.TryReserve(4096, store.Tenant) ?? throw new StorageBusyException();
            (memory.TryReserve(256 * 1024 + WorkspaceBytes, store.Tenant) ?? throw new StorageBusyException()).Dispose();
            await using var reader = await transactions.BeginAsync(store, write: false, cancellationToken).ConfigureAwait(false);
            var storage = Storage(reader);
            var started = Stopwatch.GetTimestamp();
            var now = time.GetUtcNow();
            if (!await storage.ShapeSnapshotDueAsync(shape.Handle, now.ToUnixTimeMilliseconds(), cancellationToken).ConfigureAwait(false))
            {
                return await PublishedSnapshotAsync(reader, shape.Handle, cancellationToken).ConfigureAwait(false);
            }

            var offset = await storage.ShapeSnapshotHeadAsync(cancellationToken).ConfigureAwait(false);
            var newest = await storage.NewestShapeSnapshotAsync(shape.Handle, cancellationToken).ConfigureAwait(false);
            if (newest?.Offset == offset)
            {
                return newest;
            }

            await using (var cleanup = await transactions.BeginAsync(store, write: true, cancellationToken).ConfigureAwait(false))
            {
                await Storage(cleanup).PrepareShapeSnapshotAsync(shape, offset, cancellationToken).ConfigureAwait(false);
                _ = await cleanup.CommitAsync(cancellationToken).ConfigureAwait(false);
            }

            long? after = null;
            var page = 0;
            var published = 0L;
            do
            {
                using var body = await storage.BuildShapeSnapshotPageAsync(shape, offset, page, after, memory, time, cancellationToken).ConfigureAwait(false);
                await using var writer = await transactions.BeginAsync(store, write: true, cancellationToken).ConfigureAwait(false);
                await Storage(writer).InsertShapeSnapshotPageAsync(shape, offset, page, body, now.ToUnixTimeMilliseconds(), cancellationToken).ConfigureAwait(false);
                _ = await writer.CommitAsync(cancellationToken).ConfigureAwait(false);
                published += body.Body.Length;
                after = body.NextRowid;
                page = checked(page + 1);
            }
            while (after is not null);

            metrics?.RecordSeal(published, Stopwatch.GetElapsedTime(started));
            var snapshot = new ShapeSnapshot(offset, now.ToUnixTimeMilliseconds());
            if (onSealed is not null)
            {
                try
                {
                    onSealed(store, shape, snapshot);
                }
                catch (Exception error) when (!cancellationToken.IsCancellationRequested)
                {
                    logger?.ZLogWarning(error, $"Shape snapshot callback failed; the published snapshot remains readable");
                }
            }

            return snapshot;
        }
        finally
        {
            Volatile.Write(ref _sealing, 0);
        }
    }

    public async ValueTask<ShapeSnapshotPage?> ReadPageAsync(IDatabaseStore store, string handle, long offset, int page, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(page);
        using var admission = memory.TryReserve(4096, store.Tenant) ?? throw new StorageBusyException();
        await using var transaction = await transactions.BeginAsync(store, write: false, cancellationToken).ConfigureAwait(false);
        return offset <= await FrontierAsync(transaction, cancellationToken).ConfigureAwait(false)
            ? await Storage(transaction).ReadShapeSnapshotPageAsync(handle, offset, page, memory, cancellationToken).ConfigureAwait(false)
            : null;
    }

    private static async ValueTask<ShapeSnapshot> PublishedSnapshotAsync(IStorageTransaction transaction, string handle, CancellationToken cancellationToken)
    {
        var newest = await Storage(transaction).NewestShapeSnapshotAsync(handle, cancellationToken).ConfigureAwait(false);
        return newest is not null && newest.Offset <= await FrontierAsync(transaction, cancellationToken).ConfigureAwait(false)
            ? newest : throw new StorageBusyException();
    }

    private static ValueTask<long> FrontierAsync(IStorageTransaction transaction, CancellationToken cancellationToken) =>
        (transaction as IShapeStorageTransaction ?? throw new NotSupportedException("the storage transaction does not support shapes"))
            .ReadShapeFrontierAsync(cancellationToken);

    private static IShapeSnapshotStorage Storage(IStorageTransaction transaction) =>
        transaction as IShapeSnapshotStorage ?? throw new NotSupportedException("the storage transaction does not support shape snapshots");

    internal static string PageOffset(long offset, int page) =>
        string.Create(CultureInfo.InvariantCulture, $"snap.{offset}.{page}");

    internal static byte[] EncodePage(ShapeSealDefinition shape, long offset, int page, bool more, ReadOnlySpan<byte> operations)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);
        var at = offset.ToString(CultureInfo.InvariantCulture);
        writer.WriteStartObject();
        writer.WriteString("shape_id", shape.ShapeId);
        writer.WriteString("handle", shape.Handle);
        writer.WriteString("offset", PageOffset(offset, page));
        writer.WriteString("next_offset", more ? PageOffset(offset, page + 1) : at);
        writer.WriteString("values_at", at);
        writer.WriteBoolean("up_to_date", false);
        writer.WriteStartArray("changes");
        writer.WriteStartObject();
        writer.WriteString("offset", at);
        writer.WriteStartArray("ops");
        writer.Flush();
        buffer.Write(operations);
        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.Flush();
        return buffer.WrittenSpan.ToArray();
    }
}

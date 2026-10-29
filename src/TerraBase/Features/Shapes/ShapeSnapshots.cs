using System.Buffers;
using System.Buffers.Text;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using TerraBase.Engine;
using TerraBase.Features.Documents;
using TerraBase.Features.Memory;
using ZLogger;

namespace TerraBase.Features.Shapes;
internal sealed record ShapeSnapshotDefinition(
    string Handle, string ShapeId, string Collection, string CollectionGeneration, ReadOnlyMemory<byte> ResumeScope,
    Ulid Incarnation, Func<StoredDocument, bool>? Predicate = null, DocumentFilter? OwnerFilter = null);

internal sealed record ShapeSnapshot(long Offset, long SealedAt)
{
    public string FirstOffset => ShapeSnapshots.PageOffset(Offset, null);
}
internal sealed class ShapeSnapshotPage(ReadOnlyMemory<byte> body, string? nextId, IMemoryReservation reservation) : IDisposable
{
    public ReadOnlyMemory<byte> Body { get; } = body;
    public string? NextId { get; } = nextId;
    public void Dispose() => reservation.Dispose();
}
internal interface IShapeSnapshotSealer
{
    ValueTask<ShapeSnapshot?> NewestAsync(IDatabaseStore store, string handle, CancellationToken cancellationToken);
    ValueTask<ShapeSnapshot> SealIfDueAsync(IDatabaseStore store, ShapeSnapshotDefinition shape, CancellationToken cancellationToken);
    ValueTask<ShapeSnapshotPage?> ReadPageAsync(IDatabaseStore store, string handle, long offset, string? afterId, CancellationToken cancellationToken);
}
internal interface IShapeSnapshotStorage
{
    ValueTask<ShapeSnapshot?> NewestShapeSnapshotAsync(string handle, CancellationToken cancellationToken);
    ValueTask<bool> ShapeSnapshotDueAsync(string handle, long now, CancellationToken cancellationToken);
    ValueTask<long> ShapeSnapshotFloorAsync(string handle, CancellationToken cancellationToken);
    ValueTask<(long Offset, string Cursor)> ShapeSnapshotHeadAsync(ReadOnlyMemory<byte> resumeScope, DateTimeOffset now, CancellationToken cancellationToken);
    ValueTask<ShapeSnapshotPage> BuildShapeSnapshotPageAsync(ShapeSnapshotDefinition shape, long offset, string? afterId, string cursor, IMemoryBudget memory, TimeProvider time, CancellationToken cancellationToken);
    ValueTask InsertShapeSnapshotPageAsync(ShapeSnapshotDefinition shape, long offset, int page, ShapeSnapshotPage body, long sealedAt, CancellationToken cancellationToken);
    ValueTask DeleteIncompleteShapeSnapshotsAsync(string handle, CancellationToken cancellationToken);
    ValueTask<ShapeSnapshotPage?> ReadShapeSnapshotPageAsync(string handle, long offset, string? afterId, IMemoryBudget memory, CancellationToken cancellationToken);
}
internal interface IShapeSnapshotTransactions
{
    ValueTask<IStorageTransaction> BeginAsync(IDatabaseStore store, bool write, CancellationToken cancellationToken);
}
internal sealed class ShapeSnapshots(
    IMemoryBudget memory, TimeProvider time, IShapeSnapshotTransactions transactions, IShapeMetrics? metrics = null,
    Action<IDatabaseStore, ShapeSnapshotDefinition, ShapeSnapshot>? onSealed = null, ILogger<ShapeSnapshots>? logger = null)
    : IShapeSnapshotSealer
{
    private int _sealing;
    private static readonly SearchValues<char> LowerHex = SearchValues.Create("0123456789abcdef");
    internal const int PageDocuments = 1000;
    internal const int PageBytes = 1024 * 1024;
    internal const int SingletonBytes = 8 * PageBytes;
    internal const int WorkspaceBytes = PageBytes + 64 * 1024;
    internal const int PageExamined = 10_000;
    internal const int ResealChunks = 4096;
    internal const int ResealChunksPerPage = 16;
    internal static readonly TimeSpan ResealAge = TimeSpan.FromHours(1);
    internal static readonly TimeSpan PageDeadline = TimeSpan.FromSeconds(5);

    public async ValueTask<ShapeSnapshot?> NewestAsync(IDatabaseStore store, string handle, CancellationToken cancellationToken)
    {
        using var admission = memory.TryReserve(MemoryLane.Bulk, 4096, store.Tenant) ?? throw new StorageBusyException();
        await using var transaction = await transactions.BeginAsync(store, write: false, cancellationToken).ConfigureAwait(false);
        var newest = await Storage(transaction).NewestShapeSnapshotAsync(handle, cancellationToken).ConfigureAwait(false);
        return newest is not null && newest.Offset <= await FrontierAsync(transaction, cancellationToken).ConfigureAwait(false) ? newest : null;
    }

    public async ValueTask<ShapeSnapshot> SealIfDueAsync(IDatabaseStore store, ShapeSnapshotDefinition shape, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(shape);
        if (shape.Handle.Length != 19 || !shape.Handle.StartsWith("h1-", StringComparison.Ordinal)
            || shape.Handle.AsSpan(3).ContainsAnyExcept(LowerHex)
            || shape.ShapeId.Length != 32 || shape.ShapeId.AsSpan().ContainsAnyExcept(LowerHex)
            || shape.ResumeScope.Length != 16 || !DatabaseName.TryParse(shape.Collection, out _)
            || !TerraBase.Primitives.EntityIds.IsValid(shape.CollectionGeneration))
        {
            throw new ArgumentException("invalid snapshot identity", nameof(shape));
        }

        using (var admission = memory.TryReserve(MemoryLane.Bulk, 4096, store.Tenant) ?? throw new StorageBusyException())
        await using (var probe = await transactions.BeginAsync(store, write: false, cancellationToken).ConfigureAwait(false))
        {
            if (!await DueAsync(probe, shape, time.GetUtcNow().ToUnixTimeMilliseconds(), cancellationToken).ConfigureAwait(false))
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
            using var admission = memory.TryReserve(MemoryLane.Bulk, 4096, store.Tenant) ?? throw new StorageBusyException();
            (memory.TryReserve(MemoryLane.Bulk, 256 * 1024 + WorkspaceBytes, store.Tenant) ?? throw new StorageBusyException()).Dispose();
            await using var reader = await transactions.BeginAsync(store, write: false, cancellationToken).ConfigureAwait(false);
            var storage = Storage(reader);
            var started = Stopwatch.GetTimestamp();
            var now = time.GetUtcNow();
            if (!await DueAsync(reader, shape, now.ToUnixTimeMilliseconds(), cancellationToken).ConfigureAwait(false))
            {
                return await PublishedSnapshotAsync(reader, shape.Handle, cancellationToken).ConfigureAwait(false);
            }

            using var sealMemory = memory.TryReserve(MemoryLane.Bulk, 256 * 1024, store.Tenant) ?? throw new StorageBusyException();
            var (offset, cursor) = await storage.ShapeSnapshotHeadAsync(shape.ResumeScope, now, cancellationToken).ConfigureAwait(false);

            var newest = await storage.NewestShapeSnapshotAsync(shape.Handle, cancellationToken).ConfigureAwait(false);
            if (newest?.Offset == offset)
            {
                return newest;
            }

            await using (var cleanup = await transactions.BeginAsync(store, write: true, cancellationToken).ConfigureAwait(false))
            {
                await Storage(cleanup).DeleteIncompleteShapeSnapshotsAsync(shape.Handle, cancellationToken).ConfigureAwait(false);
                _ = await cleanup.CommitAsync(cancellationToken).ConfigureAwait(false);
            }

            string? after = null;
            var page = 0;
            var published = 0L;
            do
            {
                using var body = await storage.BuildShapeSnapshotPageAsync(shape, offset, after, cursor, memory, time, cancellationToken).ConfigureAwait(false);
                await using var writer = await transactions.BeginAsync(store, write: true, cancellationToken).ConfigureAwait(false);
                await Storage(writer).InsertShapeSnapshotPageAsync(shape, offset, page, body, now.ToUnixTimeMilliseconds(), cancellationToken).ConfigureAwait(false);
                _ = await writer.CommitAsync(cancellationToken).ConfigureAwait(false);
                published += body.Body.Length;
                after = body.NextId;
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

    public async ValueTask<ShapeSnapshotPage?> ReadPageAsync(
        IDatabaseStore store, string handle, long offset, string? afterId, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        using var admission = memory.TryReserve(MemoryLane.Foreground, 4096, store.Tenant) ?? throw new StorageBusyException();
        await using var transaction = await transactions.BeginAsync(store, write: false, cancellationToken).ConfigureAwait(false);
        return offset <= await FrontierAsync(transaction, cancellationToken).ConfigureAwait(false)
            ? await Storage(transaction).ReadShapeSnapshotPageAsync(handle, offset, afterId, memory, cancellationToken).ConfigureAwait(false)
            : null;
    }

    private static async ValueTask<bool> DueAsync(IStorageTransaction transaction, ShapeSnapshotDefinition shape, long now,
        CancellationToken cancellationToken)
    {
        if (shape.OwnerFilter is not null && transaction is IShapeFreshStorage fresh
            && await Storage(transaction).NewestShapeSnapshotAsync(shape.Handle, cancellationToken).ConfigureAwait(false) is { } newest
            && newest.Offset != await fresh.ReadShapeHeadAsync(cancellationToken).ConfigureAwait(false))
        {
            return true;
        }
        return await Storage(transaction).ShapeSnapshotDueAsync(shape.Handle, now, cancellationToken).ConfigureAwait(false);
    }
    private async ValueTask<ShapeSnapshot> PublishedSnapshotAsync(IStorageTransaction transaction, string handle, CancellationToken cancellationToken)
    {
        var newest = await Storage(transaction).NewestShapeSnapshotAsync(handle, cancellationToken).ConfigureAwait(false);
        return newest is not null && newest.Offset <= await FrontierAsync(transaction, cancellationToken).ConfigureAwait(false)
            ? newest : throw new StorageBusyException();
    }

    private static ValueTask<long> FrontierAsync(IStorageTransaction transaction, CancellationToken cancellationToken) =>
        Shapes(transaction).ReadShapeFrontierAsync(cancellationToken);

    private static IShapeStorageTransaction Shapes(IStorageTransaction transaction) =>
        transaction as IShapeStorageTransaction ?? throw new NotSupportedException("the storage transaction does not support shapes");

    private static IShapeSnapshotStorage Storage(IStorageTransaction transaction) =>
        transaction as IShapeSnapshotStorage ?? throw new NotSupportedException("the storage transaction does not support shape snapshots");

    internal static string PageOffset(long offset, string? afterId) =>
        $"snap.{offset.ToString(CultureInfo.InvariantCulture)}.{(afterId is null ? "0" : Base64Url.EncodeToString(Encoding.UTF8.GetBytes(afterId)))}";

    internal static byte[] Upsert(string collection, StoredDocument document)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);
        writer.WriteStartObject();
        writer.WriteString("op", "upsert");
        writer.WriteString("id", document.Id);
        writer.WriteString("version", ETags.Version(document.Scope, collection, document.Id, document.Version, document.Json));
        writer.WritePropertyName("document");
        writer.WriteRawValue(document.Json.Span, skipInputValidation: true);
        writer.WriteEndObject();
        writer.Flush();
        return buffer.WrittenSpan.ToArray();
    }

    internal static byte[] EncodePage(
        ShapeSnapshotDefinition shape, long offset, string? afterId, string? nextId, string cursor, ReadOnlySpan<byte> operations)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);
        var at = offset.ToString(CultureInfo.InvariantCulture);
        writer.WriteStartObject();
        writer.WriteString("shape_id", shape.ShapeId);
        writer.WriteString("handle", shape.Handle);
        writer.WriteString("offset", PageOffset(offset, afterId));
        writer.WriteString("next_offset", nextId is null ? at : PageOffset(offset, nextId));
        writer.WriteString("covered_offset", at);
        writer.WriteString("values_at", at);
        writer.WriteBoolean("up_to_date", false);
        writer.WriteString("cursor_resume", cursor);
        writer.WriteStartArray("changes");
        writer.WriteStartObject();
        writer.WriteString("offset", at);
        writer.WritePropertyName("ops");
        writer.WriteRawValue(operations, skipInputValidation: true);
        writer.WriteEndObject();
        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.Flush();
        return buffer.WrittenSpan.ToArray();
    }
}

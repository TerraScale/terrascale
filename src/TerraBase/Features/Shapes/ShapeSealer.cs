using System.Diagnostics;
using TerraBase.Engine;
using TerraBase.Features.Memory;
using TerraBase.Features.Qos;
using ZLogger;

namespace TerraBase.Features.Shapes;
public sealed record ShapeSealDefinition(string ShapeId, string Handle, string Collection,
    string CollectionGeneration, ReadOnlyMemory<byte> ResumeScope, Ulid Incarnation, Func<StoredDocument, bool>? Predicate = null,
    DocumentOwnerProof? OwnerAuthorization = null);
public sealed record ShapeSealRequest(ShapeSealDefinition Shape, long Offset);
public sealed class ShapeSealedChunk(long offset, long endOffset, long valuesAt, ReadOnlyMemory<byte> body,
    IMemoryReservation reservation, long sealedAt = 0) : IDisposable
{
    public long Offset { get; } = offset;
    public long EndOffset { get; } = endOffset;
    public long ValuesAt { get; } = valuesAt;
    public ReadOnlyMemory<byte> Body { get; } = body;
    public long SealedAt { get; } = sealedAt;
    public void Dispose() => reservation.Dispose();
}
public sealed class ShapeTooLargeException() : IOException("shape_too_large: a materialized commit exceeds 8 MiB");
public sealed class ShapeSealOffsetException() : IOException("must_refetch: the shape offset is not a retained commit boundary");
internal interface IShapeStorageTransaction
{
    ValueTask<(long Offset, long Head, ShapeSealedChunk? Chunk)> SealShapeChunkAsync(ShapeSealDefinition shape, long offset, long? through,
        IMemoryBudget memory, CancellationToken cancellationToken);
    ValueTask<long> ReadShapeFrontierAsync(CancellationToken cancellationToken);
    ValueTask<ShapeSealedChunk?> ReadShapeChunkAsync(string handle, long offset, IMemoryBudget memory,
        CancellationToken cancellationToken);
}
public interface IShapeStorage
{
    ValueTask<IStorageTransaction> BeginTransactionAsync(IDatabaseStore store, bool write, CancellationToken cancellationToken);
}
public interface IShapeSealer
{
    ValueTask<long> SealUpToHeadAsync(IDatabaseStore store, ShapeSealDefinition shape, long offset,
        CancellationToken cancellationToken);
    ValueTask<ShapeSealedChunk?> ReadChunkAsync(IDatabaseStore store, string handle, long offset,
        CancellationToken cancellationToken);
    ValueTask<long> FrontierAsync(IDatabaseStore store, CancellationToken cancellationToken);
    Task RunDatabaseAsync(IDatabaseStore store, Func<IReadOnlyList<ShapeSealRequest>> shapes,
        CancellationToken cancellationToken);
}

internal sealed class ShapeSealer(
    IMemoryBudget memory, ILogger<ShapeSealer> logger, IShapeStorage storage, IShapeMetrics? metrics = null,
    Action<IDatabaseStore, ShapeSealDefinition, ShapeSealedChunk>? onSealed = null)
    : IShapeSealer
{
    private static readonly TimeSpan RefusalBackoff = TimeSpan.FromSeconds(30);
    private const int MaxWorkerRequests = 65_536;
    private const long MaxWorkerFrontierBytes = 16 * 1024 * 1024;

    public async ValueTask<long> SealUpToHeadAsync(IDatabaseStore store, ShapeSealDefinition shape, long offset,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        long? through = null;
        do
        {
            ShapeSealedChunk? chunk = null;
            try
            {
                long sealedOffset;
                var started = Stopwatch.GetTimestamp();
                var transaction = await storage.BeginTransactionAsync(store, write: true, cancellationToken).ConfigureAwait(false);
                await using (transaction.ConfigureAwait(false))
                {
                    var shapes = (IShapeStorageTransaction)transaction;
                    (sealedOffset, var head, chunk) = await shapes.SealShapeChunkAsync(shape, offset, through,
                        memory, cancellationToken).ConfigureAwait(false);
                    _ = await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                    if (chunk is not null)
                    {
                        metrics?.RecordSeal(chunk.Body.Length, Stopwatch.GetElapsedTime(started));
                    }

                    through ??= head;
                }

                if (sealedOffset == offset && offset < through.Value)
                {
                    throw new ShapeSealOffsetException();
                }

                offset = sealedOffset;
                if (onSealed is not null && chunk is not null)
                {
                    PublishSealed(store, shape, chunk, cancellationToken);
                }
            }
            finally
            {
                chunk?.Dispose();
            }
        }
        while (offset < through.Value);
        return offset;
    }

    private void PublishSealed(IDatabaseStore store, ShapeSealDefinition shape, ShapeSealedChunk chunk, CancellationToken cancellationToken)
    {
        try
        {
            onSealed!(store, shape, chunk);
        }
        catch (Exception error) when (!cancellationToken.IsCancellationRequested)
        {
            logger.ZLogWarning(error, $"Shape seal callback failed; the sealed chunk remains persisted");
        }
    }

    public async ValueTask<ShapeSealedChunk?> ReadChunkAsync(IDatabaseStore store, string handle, long offset,
        CancellationToken cancellationToken)
    {
        var transaction = await storage.BeginTransactionAsync(store, write: false, cancellationToken).ConfigureAwait(false);
        await using (transaction.ConfigureAwait(false))
        {
            var shapes = (IShapeStorageTransaction)transaction;
            var chunk = await shapes.ReadShapeChunkAsync(handle, offset, memory, cancellationToken).ConfigureAwait(false);
            if (chunk is not null && await shapes.ReadShapeFrontierAsync(cancellationToken).ConfigureAwait(false)
                < Math.Max(chunk.EndOffset, chunk.ValuesAt))
            {
                chunk.Dispose();
                return null;
            }

            return chunk;
        }
    }
    public async ValueTask<long> FrontierAsync(IDatabaseStore store, CancellationToken cancellationToken)
    {
        var transaction = await storage.BeginTransactionAsync(store, write: false, cancellationToken).ConfigureAwait(false);
        await using (transaction.ConfigureAwait(false))
        {
            return await ((IShapeStorageTransaction)transaction).ReadShapeFrontierAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task RunDatabaseAsync(IDatabaseStore store, Func<IReadOnlyList<ShapeSealRequest>> shapes,
        CancellationToken cancellationToken)
    {
        Dictionary<(string Handle, long Offset), long>? sealedTo = null;
        IMemoryReservation? frontierAdmission = null;
        try
        {
            while (!store.IsClosed)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var refused = false;
                try
                {
                    var requests = shapes();
                    var (frontier, admission) = SnapshotFrontier(store, requests, sealedTo);
                    sealedTo = frontier;
                    frontierAdmission?.Dispose();
                    frontierAdmission = admission;
                    foreach (var request in requests)
                    {
                        var key = (request.Shape.Handle, request.Offset);
                        try
                        {
                            sealedTo[key] = await SealUpToHeadAsync(store, request.Shape,
                                sealedTo.GetValueOrDefault(key, request.Offset), cancellationToken).ConfigureAwait(false);
                        }
                        catch (Exception error) when (!cancellationToken.IsCancellationRequested)
                        {
                            refused = true;
                            logger.ZLogWarning(error, $"Shape sealing refused a shape; retrying after backoff");
                            if (error is InvalidDataException)
                            {
                                break;
                            }
                        }
                    }
                }
                catch (Exception error) when (!cancellationToken.IsCancellationRequested)
                {
                    sealedTo = null;
                    frontierAdmission?.Dispose();
                    frontierAdmission = null;
                    refused = true;
                    logger.ZLogWarning(error, $"Shape enumeration failed for a database; retrying after backoff");
                }
                if (refused)
                {
                    await Task.Delay(RefusalBackoff, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                await store.WaitForCommitAsync(sequence, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            frontierAdmission?.Dispose();
        }
    }

    private (Dictionary<(string Handle, long Offset), long> Frontier, IMemoryReservation Admission) SnapshotFrontier(
        IDatabaseStore store, IReadOnlyList<ShapeSealRequest> requests, Dictionary<(string Handle, long Offset), long>? previous)
    {
        if (requests.Count > MaxWorkerRequests)
        {
            throw new QosMemoryException();
        }
        var bytes = 512L + requests.Count * 128L;
        foreach (var request in requests)
        {
            bytes += 24L + request.Shape.Handle.Length * 2L;
            if (bytes > MaxWorkerFrontierBytes)
            {
                throw new QosMemoryException();
            }
        }
        var admission = memory.TryReserve(MemoryLane.Bulk, bytes, store.Tenant) ?? throw new QosMemoryException();
        try
        {
            var frontier = new Dictionary<(string Handle, long Offset), long>(requests.Count);
            foreach (var request in requests)
            {
                var key = (request.Shape.Handle, request.Offset);
                frontier[key] = previous?.GetValueOrDefault(key, request.Offset) ?? request.Offset;
            }
            return (frontier, admission);
        }
        catch
        {
            admission.Dispose();
            throw;
        }
    }
}

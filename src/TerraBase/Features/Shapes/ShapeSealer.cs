using TerraBase.Engine;
using TerraBase.Features.Memory;
using TerraBase.Features.Qos;
using ZLogger;

namespace TerraBase.Features.Shapes;

/// <summary>A lineage-bound shape; predicates run on the writer and must be pure and bounded.</summary>
public sealed record ShapeSealDefinition(string ShapeId, string Handle, string Collection,
    string CollectionGeneration, ReadOnlyMemory<byte> ResumeScope, Func<ReadOnlyMemory<byte>, bool>? Predicate = null);

/// <summary>The next chunk to seal for an active shape, including its snapshot or resume boundary.</summary>
public sealed record ShapeSealRequest(ShapeSealDefinition Shape, long Offset);

/// <summary>Stored, immutable bytes. The caller retains admission until it finishes sending or caching them.</summary>
public sealed class ShapeSealedChunk(long offset, long endOffset, long valuesAt, ReadOnlyMemory<byte> body,
    IMemoryReservation reservation) : IDisposable
{
    /// <summary>The exclusive beginning of the covered range.</summary>
    public long Offset { get; } = offset;
    /// <summary>The inclusive commit boundary of the covered range.</summary>
    public long EndOffset { get; } = endOffset;
    /// <summary>The committed head at which documents were materialized.</summary>
    public long ValuesAt { get; } = valuesAt;
    /// <summary>The bytes persisted at sealing, never reconstructed from current documents.</summary>
    public ReadOnlyMemory<byte> Body { get; } = body;
    /// <summary>Releases admission after use.</summary>
    public void Dispose() => reservation.Dispose();
}

/// <summary>A materialized commit cannot fit in the wire contract's singleton chunk.</summary>
public sealed class ShapeTooLargeException() : IOException("shape_too_large: a materialized commit exceeds 8 MiB");

/// <summary>The requested range is unavailable or is not a retained commit boundary.</summary>
public sealed class ShapeSealOffsetException() : IOException("must_refetch: the shape offset is not a retained commit boundary");

/// <summary>The engine boundary for shape sealing; implementations execute on their transaction's lane.</summary>
internal interface IShapeStorageTransaction
{
    /// <summary>Seals one whole-commit prefix, returning its end and this transaction's committed head.</summary>
    ValueTask<(long Offset, long Head)> SealShapeChunkAsync(ShapeSealDefinition shape, long offset, long? through,
        IMemoryBudget memory, CancellationToken cancellationToken);
    /// <summary>Reads only persisted bytes, retaining the result's memory reservation.</summary>
    ValueTask<ShapeSealedChunk?> ReadShapeChunkAsync(string handle, long offset, IMemoryBudget memory,
        CancellationToken cancellationToken);
}

/// <summary>Opens derived-data transactions beneath storage decorators that track application writes.</summary>
public interface IShapeStorage
{
    /// <summary>Starts a transaction on the same database incarnation as the supplied store.</summary>
    ValueTask<IStorageTransaction> BeginTransactionAsync(IDatabaseStore store, bool write, CancellationToken cancellationToken);
}

/// <summary>Shared entry points for live flights, endpoint reads and one background worker per database.</summary>
public interface IShapeSealer
{
    /// <summary>Seals every chunk through the committed head observed on entry; returns the sealed boundary.</summary>
    ValueTask<long> SealUpToHeadAsync(IDatabaseStore store, ShapeSealDefinition shape, long offset,
        CancellationToken cancellationToken);
    /// <summary>Reads a closed chunk; an unsealed range returns null.</summary>
    ValueTask<ShapeSealedChunk?> ReadChunkAsync(IDatabaseStore store, string handle, long offset,
        CancellationToken cancellationToken);
    /// <summary>Runs one database's worker. Its owner supplies current shapes and cancels it on database deletion or shutdown.</summary>
    Task RunDatabaseAsync(IDatabaseStore store, Func<IReadOnlyList<ShapeSealRequest>> shapes,
        CancellationToken cancellationToken);
}

internal sealed class ShapeSealer(IMemoryBudget memory, ILogger<ShapeSealer> logger, IShapeStorage storage) : IShapeSealer
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
            // A standalone write transaction keeps capture suspension out of other requests' group commits.
            var transaction = await storage.BeginTransactionAsync(store, write: true, cancellationToken).ConfigureAwait(false);
            await using (transaction.ConfigureAwait(false))
            {
                var (sealedOffset, head) = await ((IShapeStorageTransaction)transaction).SealShapeChunkAsync(shape, offset, through, memory, cancellationToken).ConfigureAwait(false);
                _ = await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                through ??= head;
                offset = sealedOffset;
            }
        }
        while (offset < through.Value);
        return offset;
    }

    public async ValueTask<ShapeSealedChunk?> ReadChunkAsync(IDatabaseStore store, string handle, long offset,
        CancellationToken cancellationToken)
    {
        var transaction = await storage.BeginTransactionAsync(store, write: false, cancellationToken).ConfigureAwait(false);
        await using (transaction.ConfigureAwait(false))
        {
            return await ((IShapeStorageTransaction)transaction).ReadShapeChunkAsync(handle, offset, memory, cancellationToken).ConfigureAwait(false);
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
                var sequence = store.CommitSequence;
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
                                // Damaged capture holds this database, rather than repeating its decode for every shape.
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
            // Rebuilding releases removed handles and unused dictionary capacity with the preceding snapshot's admission.
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

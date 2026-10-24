using System.Diagnostics;
using TerraBase.Engine;
using TerraBase.Engine.Lattices;
using TerraBase.Features.Memory;
using TerraBase.Features.Qos;
using TerraBase.Features.Replication;
using ZLogger;

namespace TerraBase.Features.Shapes;

/// <summary>
/// A lineage-bound shape; predicates run on the writer and must be pure and bounded. <paramref name="Incarnation"/> is the
/// database incarnation the handle was computed under: the writer refuses when it, or the collection generation, is no longer
/// current in its transaction. The serving replica is this process's own, fixed for its lifetime.
/// </summary>
public sealed record ShapeSealDefinition(string ShapeId, string Handle, string Collection,
    string CollectionGeneration, ReadOnlyMemory<byte> ResumeScope, Ulid Incarnation, Func<StoredDocument, bool>? Predicate = null,
    DocumentOwnerProof? OwnerAuthorization = null);

/// <summary>The next chunk to seal for an active shape, including its snapshot or resume boundary.</summary>
public sealed record ShapeSealRequest(ShapeSealDefinition Shape, long Offset);

/// <summary>Stored, immutable bytes. The caller retains admission until it finishes sending or caching them.</summary>
public sealed class ShapeSealedChunk(long offset, long endOffset, long valuesAt, ReadOnlyMemory<byte> body,
    IMemoryReservation reservation, long sealedAt = 0) : IDisposable
{
    /// <summary>The exclusive beginning of the covered range.</summary>
    public long Offset { get; } = offset;
    /// <summary>The inclusive commit boundary of the covered range.</summary>
    public long EndOffset { get; } = endOffset;
    /// <summary>The committed head at which documents were materialized.</summary>
    public long ValuesAt { get; } = valuesAt;
    /// <summary>The bytes persisted at sealing, never reconstructed from current documents.</summary>
    public ReadOnlyMemory<byte> Body { get; } = body;
    /// <summary>The persisted seal time in Unix milliseconds; zero when the chunk was not read from storage.</summary>
    public long SealedAt { get; } = sealedAt;
    /// <summary>Releases admission after use.</summary>
    public void Dispose() => reservation.Dispose();
}

/// <summary>A materialized commit cannot fit in the wire contract's singleton chunk.</summary>
public sealed class ShapeTooLargeException() : IOException("shape_too_large: a materialized commit exceeds 8 MiB");

/// <summary>The requested range is unavailable or is not a retained commit boundary.</summary>
public sealed class ShapeSealOffsetException() : IOException("must_refetch: the shape offset is not a retained commit boundary");

/// <summary>The remote durable frontier did not cover the head to materialize at within <see cref="ShapeSealer.ProofTimeout"/>.</summary>
public sealed class ShapeUndurableException() : IOException("unavailable: the materialized head is not remote durable yet");

/// <summary>The engine boundary for shape sealing; implementations execute on their transaction's lane.</summary>
internal interface IShapeStorageTransaction
{
    /// <summary>
    /// Seals one whole-commit prefix, returning its end, this transaction's committed head and the bytes of the chunk this call
    /// inserted with its admission (null when reused or nothing was sealed). The caller disposes it after commit or failure.
    /// With <paramref name="remote"/> the chunk ends at or below the remote durable frontier F, at values_at = F, before the
    /// first commit with a document written above F: the end is <paramref name="offset"/> when that is the first commit.
    /// </summary>
    ValueTask<(long Offset, long Head, ShapeSealedChunk? Chunk)> SealShapeChunkAsync(ShapeSealDefinition shape, long offset, long? through, bool remote,
        IMemoryBudget memory, CancellationToken cancellationToken);
    /// <summary>The remote durable frontier with <paramref name="remote"/> (0 when none), else the committed feed head.</summary>
    ValueTask<long> ReadShapeFrontierAsync(bool remote, CancellationToken cancellationToken);
    /// <summary>
    /// Advances the remote durable frontier over captured commits already proven, joined ones whose origins are all in
    /// <paramref name="held"/> (<see cref="IRemoteDurability.HeldOriginsAsync"/>); in a write transaction. True when it moved.
    /// </summary>
    ValueTask<bool> AdvanceShapeFrontierAsync(IReadOnlySet<ReplicaId> held, CancellationToken cancellationToken);
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
    /// <summary>
    /// Seals every chunk through the committed head observed on entry; returns the sealed boundary. In a cell a chunk seals
    /// only values that are remote durable (FR-25): a commit waits for commits and frontier moves until F covers it and no
    /// write above F touches its documents, for at most 20 s.
    /// </summary>
    /// <exception cref="ShapeUndurableException">The frontier stayed below the head past the wait.</exception>
    ValueTask<long> SealUpToHeadAsync(IDatabaseStore store, ShapeSealDefinition shape, long offset,
        CancellationToken cancellationToken);
    /// <summary>Reads a closed chunk; an unsealed range, or a chunk above the durable frontier, returns null.</summary>
    ValueTask<ShapeSealedChunk?> ReadChunkAsync(IDatabaseStore store, string handle, long offset,
        CancellationToken cancellationToken);
    /// <summary>
    /// The durable frontier F (shape-reads.md section 10): the committed feed head in a cell of one, else the highest feed seq
    /// at and below which every commit is held by another region. Serve nothing above it.
    /// </summary>
    ValueTask<long> FrontierAsync(IDatabaseStore store, CancellationToken cancellationToken);
    /// <summary>Runs one database's worker. Its owner supplies current shapes and cancels it on database deletion or shutdown.</summary>
    Task RunDatabaseAsync(IDatabaseStore store, Func<IReadOnlyList<ShapeSealRequest>> shapes,
        CancellationToken cancellationToken);
}

internal sealed class ShapeSealer(
    IMemoryBudget memory, ILogger<ShapeSealer> logger, IShapeStorage storage, IRemoteDurability durability, IShapeMetrics? metrics = null,
    TimeProvider? time = null, Action<IDatabaseStore, ShapeSealDefinition, ShapeSealedChunk>? onSealed = null)
    : IShapeSealer
{
    /// <summary>Longest frontier wait of one seal: the live wait's bound, so a stalled proof releases its caller.</summary>
    internal static readonly TimeSpan ProofTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan RefusalBackoff = TimeSpan.FromSeconds(30);
    // A frontier stalled on a joined commit also moves when a link of its origin connects, which no commit signals.
    private static readonly TimeSpan FrontierPoll = TimeSpan.FromSeconds(1);
    private const int MaxWorkerRequests = 65_536;
    private const long MaxWorkerFrontierBytes = 16 * 1024 * 1024;

    public async ValueTask<long> SealUpToHeadAsync(IDatabaseStore store, ShapeSealDefinition shape, long offset,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        long? through = null;
        // Started on entry, so the bound covers the whole call; never passed to storage, so it only ends a wait.
        using var proof = new CancellationTokenSource(ProofTimeout, time ?? TimeProvider.System);
        do
        {
            ShapeSealedChunk? chunk = null;
            try
            {
                // Read before the attempt: a frontier move after it wakes the wait below.
                var sequence = store.CommitSequence;
                long sealedOffset;
                // Read before the writer is taken: joined commits of these origins are held elsewhere.
                var held = await HeldOriginsAsync(store, cancellationToken).ConfigureAwait(false);
                // A standalone write transaction keeps capture suspension out of other requests' group commits.
                var started = Stopwatch.GetTimestamp();
                var transaction = await storage.BeginTransactionAsync(store, write: true, cancellationToken).ConfigureAwait(false);
                await using (transaction.ConfigureAwait(false))
                {
                    var shapes = (IShapeStorageTransaction)transaction;
                    if (held is not null)
                    {
                        // A receive-only replica has no own ack to prove with: its seal requests move the frontier.
                        _ = await shapes.AdvanceShapeFrontierAsync(held, cancellationToken).ConfigureAwait(false);
                    }

                    (sealedOffset, var head, chunk) = await shapes.SealShapeChunkAsync(shape, offset, through,
                        durability.Confirms, memory, cancellationToken).ConfigureAwait(false);
                    _ = await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                    // Recorded after the commit: a publication is a chunk that is durable, and a reused chunk publishes nothing.
                    if (chunk is not null)
                    {
                        metrics?.RecordSeal(chunk.Body.Length, Stopwatch.GetElapsedTime(started));
                    }

                    through ??= head;
                }

                if (sealedOffset == offset && offset < through.Value)
                {
                    // The head is not remote durable yet: wait outside the writer, never weakening the gate. The attempt after
                    // the bound passes is the last one.
                    if (proof.IsCancellationRequested)
                    {
                        throw new ShapeUndurableException();
                    }

                    using (var poll = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, proof.Token))
                    {
                        _ = await Task.WhenAny(store.WaitForCommitAsync(sequence, poll.Token), Task.Delay(FrontierPoll, poll.Token)).ConfigureAwait(false);
                        await poll.CancelAsync().ConfigureAwait(false);
                    }

                    cancellationToken.ThrowIfCancellationRequested();
                    continue;
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
            // The committed transaction's own encoded bytes; publication adds no body SELECT.
            onSealed!(store, shape, chunk);
        }
        catch (Exception error) when (!cancellationToken.IsCancellationRequested)
        {
            // The chunk stays sealed; a failed notification must not fail origin reads or sealing.
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
            // Read in the same transaction: valid proof only advances; missing or damaged proof refuses the chunk.
            if (chunk is not null && await shapes.ReadShapeFrontierAsync(durability.Confirms, cancellationToken).ConfigureAwait(false)
                < Math.Max(chunk.EndOffset, chunk.ValuesAt))
            {
                chunk.Dispose();
                return null;
            }

            return chunk;
        }
    }

    // The origins to advance the frontier with, in a cell; null in a cell of one, which needs no proof.
    private async ValueTask<IReadOnlySet<ReplicaId>?> HeldOriginsAsync(IDatabaseStore store, CancellationToken cancellationToken) =>
        !durability.Confirms ? null
            : store.Tenant is { } tenant ? await durability.HeldOriginsAsync(tenant, cancellationToken).ConfigureAwait(false)
            : null;

    public async ValueTask<long> FrontierAsync(IDatabaseStore store, CancellationToken cancellationToken)
    {
        var transaction = await storage.BeginTransactionAsync(store, write: false, cancellationToken).ConfigureAwait(false);
        await using (transaction.ConfigureAwait(false))
        {
            return await ((IShapeStorageTransaction)transaction).ReadShapeFrontierAsync(durability.Confirms, cancellationToken).ConfigureAwait(false);
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
                            if (error is InvalidDataException or ShapeUndurableException)
                            {
                                // Damaged capture or a stalled frontier holds this database, rather than repeating for every shape.
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

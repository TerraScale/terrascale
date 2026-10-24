using System.Diagnostics.Metrics;
using TerraBase.Engine;
using TerraBase.Features.Memory;
using TerraBase.Features.Qos;
using TerraBase.Hosting;
using TerraBase.Tenancy;
using ZLogger;

namespace TerraBase.Features.Shapes;

/// <summary>How the sweep found a shape handle; only <see cref="Retired"/> is ever deleted.</summary>
internal enum ShapeHandleState
{
    /// <summary>Its lineage record names the current collection generation, database incarnation and replica.</summary>
    Current,

    /// <summary>Its lineage record names a lineage that is no longer current.</summary>
    Retired,

    /// <summary>Objects without a lineage record, such as those written before records existed.</summary>
    Unclassified,

    /// <summary>A lineage record that is oversized or does not decode.</summary>
    Malformed,

    /// <summary>A key that is not a canonical v1 handle.</summary>
    Unknown,
}

/// <summary>One classification and the bounded batch it deleted; <see cref="Remains"/> when rows of the handle are left.</summary>
internal sealed record ShapeSweepStep(ShapeHandleState State, int Deleted, long Bytes, bool Remains);

/// <summary>Storage operations of the sweep, kept on the engine's transaction runner.</summary>
internal interface IShapeSweepTransaction
{
    /// <summary>
    /// Up to <paramref name="limit"/> handles after <paramref name="after"/> in key order, across both object tables, their floor
    /// sentinels and lineage records, read through indexed seeks without bodies.
    /// </summary>
    ValueTask<IReadOnlyList<string>> ListShapeHandlesAsync(string? after, int limit, CancellationToken cancellationToken);

    /// <summary>
    /// In one write transaction: classifies the handle from its lineage record against the current lineage, and when it is
    /// retired deletes one bounded batch, its floor sentinel after its objects and its lineage record last.
    /// </summary>
    ValueTask<ShapeSweepStep> SweepShapeHandleAsync(string handle, int maxRows, long maxBytes, CancellationToken cancellationToken);
}

/// <summary>What one pass did, for tests and diagnostics; counts only, never tenant or shape identity.</summary>
internal sealed record ShapeSweepPass(int Databases, int Current, int Retired, int Protected, long Deleted, int Refused, int Failed);

/// <summary>
/// Retires local shape data of handles whose lineage changed (#277): every minute, for each tenant database, one bounded page
/// of handles past a per-database cursor that wraps, and at most <see cref="ShapeRetention.BatchRows"/> rows and
/// <see cref="ShapeRetention.BatchBytes"/> of bodies deleted. Independent of shape requests and of the registry; the
/// registry's retirement queue only lowers latency. Mirrored Tigris objects are not touched.
/// </summary>
internal sealed class ShapeSweeper(IStorageEngine engine, IShapeStorage storage, IMemoryBudget memory, TimeProvider time,
    IMeterFactory meters, ILogger<ShapeSweeper> logger) : BackgroundService
{
    internal static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);
    internal const int PageHandles = 16;
    // Handles examined per database per pass: current and protected handles also move the cursor.
    private const int MaxPages = 64;
    // A listed key is at most 65 UTF-16 characters; the page, its list and the step results.
    private const long PageBytes = 16 * 1024;

    private readonly Dictionary<(string, string?, string?), string?> _cursors = [];
    private readonly Counter<long> _handles = meters.Create(Telemetry.MeterName)
        .CreateCounter<long>("terrabase.shape.sweep.handles", description: "Shape handles the sweep classified, by result.");
    private readonly Counter<long> _deleted = meters.Create(Telemetry.MeterName)
        .CreateCounter<long>("terrabase.shape.sweep.deleted", description: "Rows of retired shape handles deleted by the sweep.");
    private TaskCompletionSource<ShapeSweepPass> _next = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes with the next pass to finish.</summary>
    internal Task<ShapeSweepPass> NextPassAsync() => Volatile.Read(ref _next).Task;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, time);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                ShapeSweepPass pass;
                try
                {
                    pass = await PassAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (Exception error) when (!stoppingToken.IsCancellationRequested)
                {
                    logger.ZLogWarning(error, $"Shape sweep pass failed; retrying next interval");
                    pass = new ShapeSweepPass(0, 0, 0, 0, 0, 0, 1);
                }

                Interlocked.Exchange(ref _next, new(TaskCreationOptions.RunContinuationsAsynchronously)).SetResult(pass);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutdown: an interrupted batch rolled back and the next start begins a new scan.
        }
    }

    private async Task<ShapeSweepPass> PassAsync(CancellationToken cancellationToken)
    {
        var totals = new Totals();
        HashSet<(string, string?, string?)> seen = [];
        await foreach (var database in engine.ListDatabasesAsync(cancellationToken).ConfigureAwait(false))
        {
            var key = (database.OrganizationId, database.ProjectId, database.DatabaseId);
            seen.Add(key);
            totals.Databases++;
            try
            {
                await SweepDatabaseAsync(database, key, totals, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error) when (!cancellationToken.IsCancellationRequested)
            {
                // Deleted, closed, busy or damaged: skip this database until the next pass, keeping its cursor.
                totals.Failed++;
                Record("failed");
                if (error is not (ObjectDisposedException or ShapeSealOffsetException or StorageBusyException))
                {
                    logger.ZLogWarning(error, $"Shape sweep skipped a database; retrying next interval");
                }
            }
        }

        foreach (var gone in _cursors.Keys.Where(key => !seen.Contains(key)).ToList())
        {
            _cursors.Remove(gone);
        }

        if (totals.Protected > 0)
        {
            logger.ZLogInformation($"Shape sweep protected {totals.Protected} handles without a valid lineage record");
        }

        return new ShapeSweepPass(totals.Databases, totals.Current, totals.Retired, totals.Protected, totals.Deleted, totals.Refused, totals.Failed);
    }

    private async Task SweepDatabaseAsync(TenantContext database, (string, string?, string?) key, Totals totals, CancellationToken cancellationToken)
    {
        if (await engine.OpenDatabaseAsync(database, cancellationToken).ConfigureAwait(false) is not { IsClosed: false } store)
        {
            return;
        }

        var cursor = _cursors.GetValueOrDefault(key);
        var rows = ShapeRetention.BatchRows;
        var bytes = ShapeRetention.BatchBytes;
        try
        {
            for (var page = 0; page < MaxPages; page++)
            {
                // Admitted per page under the tenant's QoS context and released before the next, so one database's large
                // retired handle cannot hold memory across the pass.
                using var admission = memory.TryReserve(MemoryLane.Bulk, PageBytes, database);
                if (admission is null)
                {
                    totals.Refused++;
                    Record("refused");
                    return;
                }

                using var qos = QosContext.Enter(database, memory);
                IReadOnlyList<string> handles;
                // The reader ends before any writer starts: the writer re-reads everything it decides on.
                await using (var reader = await storage.BeginTransactionAsync(store, write: false, cancellationToken).ConfigureAwait(false))
                {
                    handles = await Sweep(reader).ListShapeHandlesAsync(cursor, PageHandles, cancellationToken).ConfigureAwait(false);
                }

                foreach (var handle in handles)
                {
                    while (true)
                    {
                        ShapeSweepStep step;
                        await using (var writer = await storage.BeginTransactionAsync(store, write: true, cancellationToken).ConfigureAwait(false))
                        {
                            step = await Sweep(writer).SweepShapeHandleAsync(handle, rows, bytes, cancellationToken).ConfigureAwait(false);
                            _ = await writer.CommitAsync(cancellationToken).ConfigureAwait(false);
                        }

                        rows -= step.Deleted;
                        bytes -= step.Bytes;
                        totals.Deleted += step.Deleted;
                        _deleted.Add(step.Deleted);
                        if (!step.Remains)
                        {
                            Count(step.State, totals);
                            break;
                        }

                        // A batch that deleted rows continues; the next pass resumes at this handle once the budget is spent.
                        if (step.Deleted == 0 || rows <= 0 || bytes <= 0)
                        {
                            return;
                        }
                    }

                    cursor = handle;
                }

                if (handles.Count < PageHandles)
                {
                    // Wrap: the next pass rediscovers handles retired after the cursor passed them.
                    cursor = null;
                    return;
                }
            }
        }
        finally
        {
            _cursors[key] = cursor;
        }
    }

    private void Count(ShapeHandleState state, Totals totals)
    {
        switch (state)
        {
            case ShapeHandleState.Current:
                totals.Current++;
                break;
            case ShapeHandleState.Retired:
                totals.Retired++;
                break;
            default:
                totals.Protected++;
                break;
        }

        Record(state switch
        {
            ShapeHandleState.Current => "current",
            ShapeHandleState.Retired => "retired",
            ShapeHandleState.Unclassified => "unclassified",
            ShapeHandleState.Malformed => "malformed",
            _ => "unknown",
        });
    }

    private void Record(string result) => _handles.Add(1, new KeyValuePair<string, object?>("result", result));

    private static IShapeSweepTransaction Sweep(IStorageTransaction transaction) =>
        transaction as IShapeSweepTransaction ?? throw new NotSupportedException("The storage engine does not support the shape sweep.");

    private sealed class Totals
    {
        public int Databases;
        public int Current;
        public int Retired;
        public int Protected;
        public long Deleted;
        public int Refused;
        public int Failed;
    }
}

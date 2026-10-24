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

/// <summary>
/// One classification and the bounded batch it deleted: <see cref="Floor"/> is the floor a current handle's trim raised, 0 when it
/// stayed; <see cref="Remains"/> when the handle has more to delete.
/// </summary>
internal sealed record ShapeSweepStep(ShapeHandleState State, int Deleted, long Bytes, long Floor, bool Remains);

/// <summary>Storage operations of the sweep, kept on the engine's transaction runner.</summary>
internal interface IShapeSweepTransaction
{
    /// <summary>
    /// Up to <paramref name="limit"/> handles after <paramref name="after"/> in key order, across both object tables, their floor
    /// sentinels and lineage records, read through indexed seeks without bodies.
    /// </summary>
    ValueTask<IReadOnlyList<string>> ListShapeHandlesAsync(string? after, int limit, CancellationToken cancellationToken);

    /// <summary>
    /// In one write transaction: classifies the handle from its lineage record against the current lineage; a current handle gets
    /// one bounded retention trim of what was sealed before <paramref name="before"/> or lies below its floor, and a retired one
    /// one bounded drop batch, its floor sentinel after its objects and its lineage record last.
    /// </summary>
    ValueTask<ShapeSweepStep> SweepShapeHandleAsync(string handle, long before, int maxRows, long maxBytes, CancellationToken cancellationToken);
}

/// <summary>What one pass did, for tests and diagnostics; counts only, never tenant or shape identity.</summary>
internal sealed record ShapeSweepPass(int Databases, int Current, int Retired, int Protected, long Deleted, int Refused, int Failed);

/// <summary>
/// Keeps local shape data bounded with no shape request (#277): every minute, for each tenant database, bounded pages of handles
/// past a per-database cursor that wraps. Current handles get ordinary retention, retired ones (lineage changed) are dropped,
/// together at most <see cref="ShapeRetention.BatchRows"/> rows and <see cref="ShapeRetention.BatchBytes"/> of bodies per
/// database per pass. Independent of the registry, whose retirement queue only lowers latency. A raised floor of a current
/// handle is handed to the mirror; mirrored Tigris objects of retired handles are not touched.
/// </summary>
internal sealed class ShapeSweeper(IStorageEngine engine, IShapeStorage storage, ITenantDirectory tenants, IMemoryBudget memory, TimeProvider time,
    IMeterFactory meters, ILogger<ShapeSweeper> logger, Func<IDatabaseStore, string, long, CancellationToken, ValueTask>? onTrimmed = null)
    : BackgroundService
{
    internal static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);
    internal const int PageHandles = 16;
    // Handles examined per database per pass: current and protected handles also move the cursor.
    private const int MaxPages = 64;
    // A listed key is at most 65 UTF-16 characters; the page, its list and the step results.
    private const long PageBytes = 16 * 1024;

    // Created with the service, so a tick that comes before ExecuteAsync starts waiting is not lost.
    private readonly PeriodicTimer _timer = new(Interval, time);
    // Only cursors inside an inventory; a wrapped or finished database has none.
    private readonly Dictionary<(string, string?, string?), string> _cursors = [];
    private readonly Counter<long> _handles = meters.Create(Telemetry.MeterName)
        .CreateCounter<long>("terrabase.shape.sweep.handles", description: "Shape handles the sweep classified, by result.");
    private readonly Counter<long> _deleted = meters.Create(Telemetry.MeterName)
        .CreateCounter<long>("terrabase.shape.sweep.deleted", description: "Shape rows the sweep deleted, by retention or retirement.");
    private TaskCompletionSource<ShapeSweepPass> _next = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes with the next pass to finish.</summary>
    internal Task<ShapeSweepPass> NextPassAsync() => Volatile.Read(ref _next).Task;

    public override void Dispose()
    {
        _timer.Dispose();
        base.Dispose();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (await _timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                ShapeSweepPass pass;
                try
                {
                    pass = await PassAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (Exception error) when (error is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
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
        // Once per pass, as a request's retention pass computes it.
        var before = (time.GetUtcNow() - FeedCursor.Retention).ToUnixTimeMilliseconds();
        HashSet<(string, string?, string?)> seen = [];
        await foreach (var database in engine.ListDatabasesAsync(cancellationToken).ConfigureAwait(false))
        {
            var key = (database.OrganizationId, database.ProjectId, database.DatabaseId);
            seen.Add(key);
            totals.Databases++;
            try
            {
                await SweepDatabaseAsync(database, key, before, totals, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                // Deleted, closed, busy or damaged: skip this database until the next pass, keeping its cursor.
                totals.Failed++;
                Record("failed");
                if (error is not (ObjectDisposedException or StorageBusyException))
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

        return totals.Pass();
    }

    private async Task SweepDatabaseAsync(TenantContext database, (string, string?, string?) key, long before, Totals totals, CancellationToken cancellationToken)
    {
        // Listed databases carry no plan: the tenant's own plan sizes its bulk memory share and QoS context.
        if (await tenants.PlanAsync(database.OrganizationId, cancellationToken).ConfigureAwait(false) is not PlanTier plan)
        {
            totals.Refused++;
            Record("refused");
            return;
        }

        // Stores are keyed by ids; one first opened here carries the resolved plan, as one a request opens does.
        var tenant = database with { Plan = plan };
        if (await engine.OpenDatabaseAsync(tenant, cancellationToken).ConfigureAwait(false) is not { IsClosed: false } store)
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
                // handle cannot hold memory across the pass.
                using var admission = memory.TryReserve(MemoryLane.Bulk, PageBytes, tenant);
                if (admission is null)
                {
                    totals.Refused++;
                    Record("refused");
                    return;
                }

                using var qos = QosContext.Enter(tenant, memory);
                IReadOnlyList<string> handles;
                // The reader ends before any writer starts: the writer re-reads everything it decides on.
                await using (var reader = await storage.BeginTransactionAsync(store, write: false, cancellationToken).ConfigureAwait(false))
                {
                    handles = await Sweep(reader).ListShapeHandlesAsync(cursor, PageHandles, cancellationToken).ConfigureAwait(false);
                }

                foreach (var handle in handles)
                {
                    if (cursor is not null && string.CompareOrdinal(handle, cursor) <= 0)
                    {
                        // A listing that does not advance starts over next pass instead of repeating this page.
                        cursor = null;
                        return;
                    }

                    while (true)
                    {
                        // The budget is the database's for the whole pass, across trims and drops; checked before every writer.
                        if (rows <= 0 || bytes <= 0)
                        {
                            return;
                        }

                        ShapeSweepStep step;
                        try
                        {
                            await using var writer = await storage.BeginTransactionAsync(store, write: true, cancellationToken).ConfigureAwait(false);
                            step = await Sweep(writer).SweepShapeHandleAsync(handle, before, rows, bytes, cancellationToken).ConfigureAwait(false);
                            _ = await writer.CommitAsync(cancellationToken).ConfigureAwait(false);
                        }
                        catch (InvalidDataException)
                        {
                            // Rolled back: a damaged row of this handle protects it without pinning the database's cursor.
                            Count(ShapeHandleState.Malformed, totals);
                            break;
                        }

                        rows -= step.Deleted;
                        bytes -= step.Bytes;
                        totals.Deleted += step.Deleted;
                        _deleted.Add(step.Deleted);
                        if (step.Floor > 0 && onTrimmed is not null)
                        {
                            // After the trim's writer is disposed; the mirror queues it and rereads the floor.
                            await onTrimmed(store, handle, step.Floor, cancellationToken).ConfigureAwait(false);
                        }

                        if (!step.Remains)
                        {
                            Count(step.State, totals);
                            break;
                        }

                        // A batch that deleted rows continues; one that fit nothing resumes at this handle next pass.
                        if (step.Deleted == 0)
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
            if (cursor is null)
            {
                _cursors.Remove(key);
            }
            else
            {
                _cursors[key] = cursor;
            }
        }
    }

    private void Count(ShapeHandleState state, Totals totals)
    {
        switch (state)
        {
            case ShapeHandleState.Current:
                totals.Current++;
                Record("current");
                break;
            case ShapeHandleState.Retired:
                totals.Retired++;
                Record("retired");
                break;
            default:
                totals.Protected++;
                Record(state switch
                {
                    ShapeHandleState.Unclassified => "unclassified",
                    ShapeHandleState.Malformed => "malformed",
                    _ => "unknown",
                });
                break;
        }
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

        public ShapeSweepPass Pass() => new(Databases, Current, Retired, Protected, Deleted, Refused, Failed);
    }
}

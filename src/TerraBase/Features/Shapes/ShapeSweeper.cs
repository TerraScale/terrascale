using System.Diagnostics.Metrics;
using TerraBase.Engine;
using TerraBase.Features.Memory;
using TerraBase.Features.Qos;
using TerraBase.Hosting;
using TerraBase.Tenancy;
using ZLogger;

namespace TerraBase.Features.Shapes;

internal enum ShapeHandleState
{
    Current,

    Retired,

    Unclassified,

    Malformed,

    Unknown,
}

internal sealed record ShapeSweepStep(ShapeHandleState State, int Deleted, long Bytes, long Floor, bool Remains);

internal interface IShapeSweepTransaction
{
    ValueTask<IReadOnlyList<string>> ListShapeHandlesAsync(string? after, int limit, CancellationToken cancellationToken);

    ValueTask<ShapeSweepStep> SweepShapeHandleAsync(string handle, long before, int maxRows, long maxBytes, CancellationToken cancellationToken);
}

internal sealed record ShapeSweepPass(int Databases, int Current, int Retired, int Protected, long Deleted, int Refused, int Failed);

internal sealed class ShapeSweeper(IStorageEngine engine, IShapeStorage storage, ITenantDirectory tenants, IMemoryBudget memory, TimeProvider time,
    IMeterFactory meters, ILogger<ShapeSweeper> logger, Func<IDatabaseStore, string, long, CancellationToken, ValueTask>? onTrimmed = null)
    : BackgroundService
{
    internal static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);
    internal const int PageHandles = 16;
    private const int MaxPages = 64;
    private const long PageBytes = 16 * 1024;

    private readonly PeriodicTimer _timer = new(Interval, time);
    private readonly Dictionary<(string, string?, string?), string> _cursors = [];
    private readonly Counter<long> _handles = meters.Create(Telemetry.MeterName)
        .CreateCounter<long>("terrabase.shape.sweep.handles", description: "Shape handles the sweep classified, by result.");
    private readonly Counter<long> _deleted = meters.Create(Telemetry.MeterName)
        .CreateCounter<long>("terrabase.shape.sweep.deleted", description: "Shape rows the sweep deleted, by retention or retirement.");
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
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task<ShapeSweepPass> PassAsync(CancellationToken cancellationToken)
    {
        var totals = new Totals();
        var before = (time.GetUtcNow() - ShapeRetention.Age).ToUnixTimeMilliseconds();
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
        if (await tenants.PlanAsync(database.OrganizationId, cancellationToken).ConfigureAwait(false) is not PlanTier plan)
        {
            totals.Refused++;
            Record("refused");
            return;
        }

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
                using var admission = memory.TryReserve(PageBytes, tenant);
                if (admission is null)
                {
                    totals.Refused++;
                    Record("refused");
                    return;
                }

                using var qos = QosContext.Enter(tenant, memory);
                IReadOnlyList<string> handles;
                await using (var reader = await storage.BeginTransactionAsync(store, write: false, cancellationToken).ConfigureAwait(false))
                {
                    handles = await Sweep(reader).ListShapeHandlesAsync(cursor, PageHandles, cancellationToken).ConfigureAwait(false);
                }

                foreach (var handle in handles)
                {
                    if (cursor is not null && string.CompareOrdinal(handle, cursor) <= 0)
                    {
                        cursor = null;
                        return;
                    }

                    while (true)
                    {
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
                            Count(ShapeHandleState.Malformed, totals);
                            break;
                        }

                        rows -= step.Deleted;
                        bytes -= step.Bytes;
                        totals.Deleted += step.Deleted;
                        _deleted.Add(step.Deleted);
                        if (step.Floor > 0 && onTrimmed is not null)
                        {
                            await onTrimmed(store, handle, step.Floor, cancellationToken).ConfigureAwait(false);
                        }

                        if (!step.Remains)
                        {
                            Count(step.State, totals);
                            break;
                        }

                        if (step.Deleted == 0)
                        {
                            return;
                        }
                    }

                    cursor = handle;
                }

                if (handles.Count < PageHandles)
                {
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

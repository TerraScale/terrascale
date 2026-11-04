using System.Globalization;
using TerraBase.Engine;
using TerraBase.Errors;
using TerraBase.Features.Memory;
using TerraBase.Features.Qos;
using TerraBase.Tenancy;
using ZLogger;

namespace TerraBase.Features.Shapes;

internal sealed class ShapeService(
    IStorageEngine engine, IShapeSealer sealer, IShapeSnapshotSealer snapshots, IShapeStorage storage, IShapeRetention retention,
    ShapeRegistry registry, ShapeLiveGate gate, IShapeFlights<ShapeRead> flights, IMemoryBudget memory, IHostApplicationLifetime lifetime,
    ILogger<ShapeService> logger) : IShapeLineages, IShapeSnapshots, IShapeChunks, IShapeLiveTail
{
    private int _maintaining;
    private readonly ClosedChunks _closed = new(memory);
    private static readonly QuotaExceeded Busy = new("shape reads are at capacity; retry later", "shape_busy");
    private static readonly NotFound UnknownDatabase = new("the database does not exist", "unknown_database");
    private static readonly NotFound UnknownTable = new("the table does not exist", "unknown_table");
    private static readonly Validation UnsupportedTable = new("shapes read ordinary rowid tables", "unsupported_table");

    public async ValueTask<LineageResult> ResolveAsync(TenantContext database, string table, CancellationToken cancellationToken)
    {
        try
        {
            if (await engine.OpenDatabaseAsync(database, cancellationToken).ConfigureAwait(false) is not { } store)
            {
                return UnknownDatabase;
            }

            var resolved = await CurrentAsync(store, database, table, cancellationToken).ConfigureAwait(false);
            if (resolved is not ResolvedShape shape)
            {
                return resolved;
            }

            var definition = ShapeHandle.Definition(store, shape.Scope);
            if (registry.CurrentHandle(database, table) != definition.Handle)
            {
                var head = await ReadHeadAsync(store, cancellationToken).ConfigureAwait(false);
                if (registry.Track(database, definition, head) is { } full)
                {
                    return full;
                }
            }

            ScheduleMaintenance(shape.Scope, store, shape.Lineage);
            return shape;
        }
        catch (Exception error) when (error is ShapeSealOffsetException or ObjectDisposedException or StorageBusyException or QosMemoryException)
        {
            return new Unavailable("shape storage changed or is busy; retry later");
        }
    }

    private async ValueTask<LineageResult> CurrentAsync(IDatabaseStore store, TenantContext database, string table, CancellationToken cancellationToken)
    {
        ShapeTableState state;
        await using (var reader = await storage.BeginTransactionAsync(store, write: false, cancellationToken).ConfigureAwait(false))
        {
            state = await Tables(reader).ReadShapeTableAsync(table, cancellationToken).ConfigureAwait(false);
        }

        if (state.Status == ShapeTableStatus.Stale)
        {
            await using var writer = await storage.BeginTransactionAsync(store, write: true, cancellationToken).ConfigureAwait(false);
            state = await Tables(writer).RenewShapeTableAsync(table, cancellationToken).ConfigureAwait(false);
            _ = await writer.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        return state switch
        {
            { Status: ShapeTableStatus.Missing } => UnknownTable,
            { Status: ShapeTableStatus.Unsupported } => UnsupportedTable,
            { Status: ShapeTableStatus.Current, Generation: { } generation } => Resolved(store, new ShapeScope(database, table, generation)),
            _ => new Unavailable("the table changed while resolving its shape; retry later"),
        };
    }

    private static ResolvedShape Resolved(IDatabaseStore store, ShapeScope scope)
    {
        var handle = ShapeHandle.Compute(store, scope.Table, scope.Generation);
        return new ResolvedShape(scope, new ShapeLineage(handle.ShapeId, handle.Handle));
    }

    private async ValueTask<ShapeRead?> StaleAsync(IDatabaseStore store, ShapeScope scope, ShapeLineage lineage, CancellationToken cancellationToken) =>
        await CurrentAsync(store, scope.Database, scope.Table, cancellationToken).ConfigureAwait(false) switch
        {
            ResolvedShape current when current.Lineage.Handle == lineage.Handle => null,
            ResolvedShape current => new MustRefetch("the shape lineage changed", current.Lineage),
            NotFound missing => missing,
            Unavailable unavailable => unavailable,
            Validation => new MustRefetch("the table no longer supports shapes"),
            QuotaExceeded busy => busy,
        };

    public async ValueTask<SnapshotResult> NewestAsync(ShapeScope scope, ShapeLineage lineage, CancellationToken cancellationToken)
    {
        if (await engine.OpenDatabaseAsync(scope.Database, cancellationToken).ConfigureAwait(false) is not { } store)
        {
            return UnknownDatabase;
        }

        try
        {
            if (await StaleAsync(store, scope, lineage, cancellationToken).ConfigureAwait(false) is { } stale)
            {
                return Snapshot(stale);
            }

            var snapshot = await snapshots.SealIfDueAsync(store, ShapeHandle.Definition(store, scope), cancellationToken).ConfigureAwait(false);
            if (await StaleAsync(store, scope, lineage, cancellationToken).ConfigureAwait(false) is { } changed)
            {
                return Snapshot(changed);
            }

            return snapshot.Offset;
        }
        catch (ShapeSealOffsetException)
        {
            return new MustRefetch("the shape lineage changed while sealing the snapshot");
        }
        catch (ShapeTooLargeException)
        {
            return new Unavailable("a snapshot row exceeds 8 MiB", "shape_too_large");
        }
        catch (ObjectDisposedException)
        {
            return new Unavailable("the database is closing; retry later");
        }
        catch (Exception error) when (error is StorageBusyException or QosMemoryException)
        {
            return Busy;
        }
    }

    private static SnapshotResult Snapshot(ShapeRead read) => read switch
    {
        MustRefetch refetch => refetch,
        NotFound missing => missing,
        Unavailable unavailable => unavailable,
        QuotaExceeded busy => busy,
        _ => throw new System.Diagnostics.UnreachableException(),
    };

    public async ValueTask<ShapeRead> PageAsync(ShapeScope scope, ShapeLineage lineage, long snapshot, int page, CancellationToken cancellationToken)
    {
        if (await engine.OpenDatabaseAsync(scope.Database, cancellationToken).ConfigureAwait(false) is null)
        {
            return UnknownDatabase;
        }

        var request = new ShapeFlightRequest(scope.Database, lineage.Handle, ShapeSnapshots.PageOffset(snapshot, page), false);
        return Join(await flights.ReadAsync(request, NotParked, Read, cancellationToken).ConfigureAwait(false));

        async ValueTask<ShapeRead> Read(IDatabaseStore current, CancellationToken token)
        {
            return Detach(await ReadPageAtAsync(current, scope, lineage, snapshot, page, token).ConfigureAwait(false));
        }
    }

    private async ValueTask<ShapeRead> ReadPageAtAsync(IDatabaseStore store, ShapeScope scope, ShapeLineage lineage, long snapshot, int page, CancellationToken cancellationToken)
    {
        try
        {
            if (await StaleAsync(store, scope, lineage, cancellationToken).ConfigureAwait(false) is { } stale)
            {
                return stale;
            }

            if (await retention.CheckOffsetAsync(store, lineage.Handle, snapshot, cancellationToken).ConfigureAwait(false) is { } below)
            {
                return new MustRefetch(below.Message);
            }

            if (await snapshots.ReadPageAsync(store, lineage.Handle, snapshot, page, cancellationToken).ConfigureAwait(false) is not { } body)
            {
                return new MustRefetch("the snapshot page is not sealed for this handle");
            }

            var next = body.NextRowid is null ? Decimal(snapshot) : ShapeSnapshots.PageOffset(snapshot, page + 1);
            return new ShapeBody(body.Body, next, false, body);
        }
        catch (ShapeSealOffsetException)
        {
            return new MustRefetch("the shape lineage changed while reading the snapshot");
        }
        catch (ObjectDisposedException)
        {
            return new Unavailable("the database is closing; retry later");
        }
        catch (Exception error) when (error is StorageBusyException or QosMemoryException)
        {
            return Busy;
        }
    }

    public async ValueTask<ShapeRead> ReadAsync(ShapeScope scope, ShapeLineage lineage, long offset, CancellationToken cancellationToken)
    {
        if (await engine.OpenDatabaseAsync(scope.Database, cancellationToken).ConfigureAwait(false) is null)
        {
            return UnknownDatabase;
        }

        var request = new ShapeFlightRequest(scope.Database, lineage.Handle, Decimal(offset), false);
        return Join(await flights.ReadAsync(request, NotParked, Read, cancellationToken).ConfigureAwait(false));

        async ValueTask<ShapeRead> Read(IDatabaseStore current, CancellationToken token)
        {
            return Detach(await ReadAtAsync(current, scope, lineage, offset, fresh: false, token).ConfigureAwait(false));
        }
    }

    public async ValueTask<ShapeRead> WaitAsync(ShapeScope scope, ShapeLineage lineage, long offset, CancellationToken cancellationToken)
    {
        if (await engine.OpenDatabaseAsync(scope.Database, cancellationToken).ConfigureAwait(false) is null)
        {
            return UnknownDatabase;
        }

        var key = new GateKey(scope.Database.OrganizationId, scope.Database.ProjectId, scope.Database.DatabaseId, lineage.Handle, offset);
        var group = gate.Enter(key);
        var parked = false;
        var admitted = false;
        try
        {
            var request = new ShapeFlightRequest(scope.Database, lineage.Handle, Decimal(offset), true, () =>
            {
                admitted = true;
                gate.Admit(group);
            });
            return Join(await flights.ReadAsync(request, Probe, Read, cancellationToken).ConfigureAwait(false));

            async ValueTask<bool> Probe(IDatabaseStore current, CancellationToken token)
            {
                try
                {
                    parked = await sealer.FrontierAsync(current, token).ConfigureAwait(false) == offset;
                }
                catch (Exception error) when (error is ShapeSealOffsetException or ObjectDisposedException)
                {
                    return false;
                }

                if (parked)
                {
                    registry.Advance(scope.Database, scope.Table, offset);
                    gate.Park(group);
                }

                return parked;
            }

            async ValueTask<ShapeRead> Read(IDatabaseStore current, CancellationToken token)
            {
                gate.Unpark(group);
                return Detach(await ReadAtAsync(current, scope, lineage, offset, fresh: parked, token).ConfigureAwait(false));
            }
        }
        finally
        {
            gate.Leave(key, group, admitted);
        }
    }

    private async ValueTask<ShapeRead> ReadAtAsync(IDatabaseStore store, ShapeScope scope, ShapeLineage lineage, long offset, bool fresh, CancellationToken cancellationToken)
    {
        try
        {
            if (await StaleAsync(store, scope, lineage, cancellationToken).ConfigureAwait(false) is { } stale)
            {
                return stale;
            }

            var definition = ShapeHandle.Definition(store, scope);
            var head = await sealer.FrontierAsync(store, cancellationToken).ConfigureAwait(false);
            if (offset > head)
            {
                return new MustRefetch("the offset is above the head");
            }

            if (await retention.CheckOffsetAsync(store, lineage.Handle, offset, cancellationToken).ConfigureAwait(false) is { } below)
            {
                return new MustRefetch(below.Message);
            }

            var read = fresh || offset == head
                ? await FreshAsync(store, definition, offset, cancellationToken).ConfigureAwait(false)
                : await ClosedAsync(store, definition, offset, cancellationToken).ConfigureAwait(false);
            if (read is ShapeBody body && await StaleAsync(store, scope, lineage, cancellationToken).ConfigureAwait(false) is { } changed)
            {
                body.Lease?.Dispose();
                return changed;
            }

            return read;
        }
        catch (ShapeSealOffsetException)
        {
            return await StaleAsync(store, scope, lineage, cancellationToken).ConfigureAwait(false)
                ?? new MustRefetch("the offset is not a retained commit boundary");
        }
        catch (ShapeTooLargeException)
        {
            return new Unavailable("a commit exceeds 8 MiB of shape operations", "shape_too_large");
        }
        catch (ObjectDisposedException)
        {
            return new Unavailable("the database is closing; retry later");
        }
        catch (Exception error) when (error is StorageBusyException or QosMemoryException)
        {
            return Busy;
        }
    }

    private async ValueTask<ShapeRead> ClosedAsync(IDatabaseStore store, ShapeSealDefinition definition, long offset, CancellationToken cancellationToken)
    {
        if (_closed.Get(definition.Handle, offset) is { } cached)
        {
            return cached;
        }

        var chunk = await sealer.ReadChunkAsync(store, definition.Handle, offset, cancellationToken).ConfigureAwait(false);
        if (chunk is null)
        {
            _ = await sealer.SealUpToHeadAsync(store, definition, offset, cancellationToken).ConfigureAwait(false);
            chunk = await sealer.ReadChunkAsync(store, definition.Handle, offset, cancellationToken).ConfigureAwait(false);
        }

        if (chunk is null)
        {
            return new MustRefetch("the offset is not a retained commit boundary");
        }

        var body = new ShapeBody(chunk.Body, Decimal(chunk.EndOffset), false, chunk);
        _closed.Add(definition.Handle, offset, body);
        return body;
    }

    private async ValueTask<ShapeRead> FreshAsync(IDatabaseStore store, ShapeSealDefinition definition, long offset, CancellationToken cancellationToken)
    {
        var fresh = await ReadFreshAsync(store, definition, offset, cancellationToken).ConfigureAwait(false);
        if (fresh is null)
        {
            _ = await sealer.SealUpToHeadAsync(store, definition, offset, cancellationToken).ConfigureAwait(false);
            fresh = await ReadFreshAsync(store, definition, offset, cancellationToken).ConfigureAwait(false);
        }

        return fresh is null ? new MustRefetch("the offset is not a retained commit boundary") : fresh;
    }

    private async ValueTask<ShapeBody?> ReadFreshAsync(IDatabaseStore store, ShapeSealDefinition definition, long offset, CancellationToken cancellationToken)
    {
        await using var transaction = await storage.BeginTransactionAsync(store, write: false, cancellationToken).ConfigureAwait(false);
        return await Fresh(transaction).ReadShapeFreshAsync(definition, offset, memory, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<long> ReadHeadAsync(IDatabaseStore store, CancellationToken cancellationToken)
    {
        await using var transaction = await storage.BeginTransactionAsync(store, write: false, cancellationToken).ConfigureAwait(false);
        return await Fresh(transaction).ReadShapeHeadAsync(cancellationToken).ConfigureAwait(false);
    }

    private static IShapeFreshStorage Fresh(IStorageTransaction transaction) =>
        transaction as IShapeFreshStorage ?? throw new NotSupportedException("the storage transaction does not support shape fresh bodies");

    private static IShapeTableStorage Tables(IStorageTransaction transaction) =>
        transaction as IShapeTableStorage ?? throw new NotSupportedException("the storage transaction does not support shape tables");

    private static string Decimal(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static ShapeRead Detach(ShapeRead read)
    {
        if (read is not ShapeBody body)
        {
            return read;
        }

        using (body.Lease)
        {
            try
            {
                QosContext.ReserveResult(body.Json.Length);
            }
            catch (QosMemoryException)
            {
                return Busy;
            }

            return new ShapeBody(body.Json.ToArray(), body.NextOffset, body.UpToDate);
        }
    }

    private static ShapeRead Hold(ShapeFlight<ShapeRead> flight)
    {
        if (flight.Value is not ShapeBody body)
        {
            flight.Dispose();
            return flight.Value;
        }

        return new ShapeBody(body.Json, body.NextOffset, body.UpToDate, flight);
    }

    private static readonly ShapeHeadProbe NotParked = static (_, _) => ValueTask.FromResult(false);

    private static ShapeRead Join(ShapeFlightResult<ShapeRead> result) => result switch
    {
        ShapeFlight<ShapeRead> flight => Hold(flight),
        QuotaExceeded refused => refused,
        NotFound => UnknownDatabase,
        Unavailable stuck => stuck,
    };

    private void ScheduleMaintenance(ShapeScope scope, IDatabaseStore store, ShapeLineage lineage)
    {
        if (Interlocked.CompareExchange(ref _maintaining, 1, 0) != 0)
        {
            return;
        }

        var admission = memory.TryReserve(MemoryLane.Bulk, 64 * 1024, scope.Database);
        if (admission is null)
        {
            Volatile.Write(ref _maintaining, 0);
            return;
        }

        _ = Task.Run(async () =>
        {
            using (admission)
            using (var qos = QosContext.Enter(scope.Database, memory))
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
            using (var stop = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, lifetime.ApplicationStopping))
            {
                try
                {
                    if (await StaleAsync(store, scope, lineage, stop.Token).ConfigureAwait(false) is null)
                    {
                        await MaintainAsync(scope, store, lineage, stop.Token).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested)
                {
                }
                catch (Exception error)
                {
                    logger.ZLogWarning(error, $"Shape maintenance deferred");
                }
                finally
                {
                    Volatile.Write(ref _maintaining, 0);
                }
            }
        });
    }

    private async Task MaintainAsync(ShapeScope scope, IDatabaseStore store, ShapeLineage lineage, CancellationToken cancellationToken)
    {
        if (registry.TakeRetired(scope.Database) is { } retired)
        {
            var requeued = false;
            try
            {
                if (await CurrentAsync(store, scope.Database, retired.Table, cancellationToken).ConfigureAwait(false) is ResolvedShape current
                    && current.Lineage.Handle == retired.Handle)
                {
                    return;
                }

                if (await retention.DropHandleAsync(store, retired.Handle, cancellationToken).ConfigureAwait(false) > 0)
                {
                    registry.Retire(retired);
                    requeued = true;
                }
            }
            catch
            {
                registry.Retire(retired);
                requeued = true;
                throw;
            }
            finally
            {
                if (!requeued)
                {
                    retired.Dispose();
                }
            }
        }

        if (registry.ClaimTrim(scope.Database, scope.Table))
        {
            try
            {
                _ = await retention.RunPassAsync(store, lineage.Handle, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error) when (error is StorageBusyException or QosMemoryException)
            {
            }
        }
    }
}

internal sealed class ClosedChunks(IMemoryBudget memory)
{
    private const int MaxEntries = 512;
    private const long MaxBytes = 16 * 1024 * 1024;
    private readonly Lock _gate = new();
    private readonly Dictionary<(string Handle, long Offset), Entry> _entries = [];
    private readonly Queue<(string Handle, long Offset)> _order = new();
    private long _bytes;

    public ShapeBody? Get(string handle, long offset)
    {
        lock (_gate)
        {
            return _entries.TryGetValue((handle, offset), out var entry) ? new ShapeBody(entry.Json, entry.NextOffset, false) : null;
        }
    }

    public void Add(string handle, long offset, ShapeBody body)
    {
        if (body.Json.Length > MaxBytes / 8 || memory.TryReserve(MemoryLane.Bulk, body.Json.Length, null) is not { } admission)
        {
            return;
        }

        var entry = new Entry(body.Json.ToArray(), body.NextOffset, admission);
        lock (_gate)
        {
            if (!_entries.TryAdd((handle, offset), entry))
            {
                admission.Dispose();
                return;
            }

            _order.Enqueue((handle, offset));
            _bytes += entry.Json.Length;
            while (_entries.Count > MaxEntries || _bytes > MaxBytes)
            {
                var oldest = _order.Dequeue();
                if (_entries.Remove(oldest, out var evicted))
                {
                    _bytes -= evicted.Json.Length;
                    evicted.Admission.Dispose();
                }
            }
        }
    }

    private sealed record Entry(byte[] Json, string NextOffset, IMemoryReservation Admission);
}

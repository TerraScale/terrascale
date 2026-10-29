using System.Security.Cryptography;
using System.Text;
using TerraBase.Engine;
using TerraBase.Errors;
using TerraBase.Features.Documents;
using TerraBase.Features.Memory;
using TerraBase.Features.Qos;
using TerraBase.Tenancy;
using ZLogger;

namespace TerraBase.Features.Shapes;

internal sealed class ShapeService(
    IStorageEngine engine, IDocumentService documents, IShapeSealer sealer, IShapeSnapshotSealer snapshots,
    IShapeStorage storage, IShapeRetention retention, IShapeFilterRegistry filters, ShapeRegistry registry, ShapeLiveGate gate,
    IShapeFlights<ShapeRead> flights, IMemoryBudget memory, IHostApplicationLifetime lifetime, ILogger<ShapeService> logger) : IShapeLineages, IShapeSnapshots, IShapeChunks, IShapeLiveTail, IShapeResume
{
    private int _maintaining;
    private readonly ClosedChunks _closed = new(memory);
    private static readonly QuotaExceeded Busy = new("shape reads are at capacity; retry later", "shape_busy");
    private static readonly NotFound UnknownDatabase = new("the database does not exist", "unknown_database");
    internal static byte[] ResumeScope(TenantContext tenant, CollectionInfo collection, string canonical)
    {
        var fields = string.Join('\0', tenant.OrganizationId, tenant.ProjectId, tenant.DatabaseId, collection.Name, collection.Id, canonical, "1");
        return SHA256.HashData(Encoding.UTF8.GetBytes("TB-SHAPE-RESUME-V1\0" + fields)).AsSpan(0, 16).ToArray();
    }
    public async ValueTask<LineageResult> ResolveAsync(ShapeScope scope, CancellationToken cancellationToken)
    {
        try
        {
            return await ResolveCoreAsync(scope, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is ShapeSealOffsetException or ObjectDisposedException or StorageBusyException or QosMemoryException)
        {
            return new Unavailable("shape storage changed or is busy; retry later");
        }
    }

    private async ValueTask<LineageResult> ResolveCoreAsync(ShapeScope scope, CancellationToken cancellationToken)
    {
        if (await engine.OpenDatabaseAsync(scope.Database, cancellationToken).ConfigureAwait(false) is not { } store)
        {
            return UnknownDatabase;
        }

        var identity = ShapeHandle.ComputeCanonical(store, scope.Collection, scope.CanonicalFilter);
        var lineage = new ShapeLineage(identity.ShapeId, identity.Handle);
        var collection = scope.Collection.Name;
        if (scope.Filter.Canonical.Length > 0)
        {
            if (!scope.Bootstrap && filters.Check(scope.Database, collection, scope.Filter) is not null)
            {
                return new Evicted(lineage);
            }

            switch (filters.Admit(scope.Database, collection, scope.Filter))
            {
                case QuotaExceeded refused:
                    return refused;
                case ShapeAdmitted admitted:
                    registry.Forget(admitted.Evicted);
                    break;
            }
        }

        if (await CurrentAsync(store, scope, lineage, cancellationToken).ConfigureAwait(false) is { } stale)
        {
            return stale switch
            {
                MustRefetch changed => new Evicted(changed.Lineage ?? lineage),
                Forbidden forbidden => forbidden,
                NotFound missing => missing,
                Unavailable unavailable => unavailable,
                _ => throw new System.Diagnostics.UnreachableException(),
            };
        }

        if (registry.CurrentHandle(scope.Database, collection, scope.CanonicalFilter) != lineage.Handle)
        {
            var head = await ReadHeadAsync(store, cancellationToken).ConfigureAwait(false);
            if (registry.Track(scope.Database, collection, scope.Filter, Definition(store, scope, lineage), head) is { } full)
            {
                return full;
            }
        }

        ScheduleMaintenance(scope, store, lineage);
        return lineage;
    }
    public async ValueTask<LineageResult> ResolveHandleAsync(TenantContext database, CollectionInfo collection, string handle, CancellationToken cancellationToken)
    {
        try
        {
            return await ResolveHandleCoreAsync(database, collection, handle, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is ShapeSealOffsetException or ObjectDisposedException or StorageBusyException or QosMemoryException)
        {
            return new Unavailable("shape storage changed or is busy; retry later");
        }
    }

    private async ValueTask<LineageResult> ResolveHandleCoreAsync(TenantContext database, CollectionInfo collection, string handle, CancellationToken cancellationToken)
    {
        if (await engine.OpenDatabaseAsync(database, cancellationToken).ConfigureAwait(false) is not { } store)
        {
            return UnknownDatabase;
        }

        if (CollectionRuleAccess.EndUser)
        {
            return ShapeAccess.Denied;
        }

        var unfiltered = ShapeHandle.ComputeCanonical(store, collection, string.Empty);
        var current = new ShapeLineage(unfiltered.ShapeId, unfiltered.Handle);
        if (registry.FilteredShape(database, collection.Name, handle) is not { } filter)
        {
            return current;
        }
        if (filters.Check(database, collection.Name, filter) is not null)
        {
            return new Evicted(current);
        }

        var filtered = ShapeHandle.ComputeCanonical(store, collection, filter.Canonical);
        return filtered.Handle == handle ? new ShapeLineage(filtered.ShapeId, filtered.Handle) : current;
    }
    public async ValueTask<SnapshotResult> NewestAsync(ShapeScope scope, ShapeLineage lineage, CancellationToken cancellationToken)
    {
        if (await engine.OpenDatabaseAsync(scope.Database, cancellationToken).ConfigureAwait(false) is not { } store)
        {
            return UnknownDatabase;
        }

        try
        {
            if (await CurrentAsync(store, scope, lineage, cancellationToken).ConfigureAwait(false) is { } stale)
            {
                return stale switch
                {
                    MustRefetch staleLineage => staleLineage,
                    Forbidden forbidden => forbidden,
                    NotFound missing => missing,
                    Unavailable unavailable => unavailable,
                    _ => throw new System.Diagnostics.UnreachableException(),
                };
            }
            var snapshot = await snapshots.SealIfDueAsync(store, SnapshotDefinition(store, scope, lineage), cancellationToken).ConfigureAwait(false);
            if (await CurrentAsync(store, scope, lineage, cancellationToken).ConfigureAwait(false) is { } changed)
            {
                return changed switch
                {
                    MustRefetch refetch => refetch,
                    Forbidden forbidden => forbidden,
                    NotFound missing => missing,
                    Unavailable unavailable => unavailable,
                    _ => throw new System.Diagnostics.UnreachableException(),
                };
            }

            return snapshot.Offset;
        }
        catch (ShapeSealOffsetException)
        {
            return new MustRefetch("the shape lineage changed while sealing the snapshot");
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
    public async ValueTask<ShapeRead> PageAsync(ShapeScope scope, ShapeLineage lineage, long snapshot, string? afterId, CancellationToken cancellationToken)
    {
        if (await engine.OpenDatabaseAsync(scope.Database, cancellationToken).ConfigureAwait(false) is not { } store)
        {
            return UnknownDatabase;
        }

        var request = new ShapeFlightRequest(scope.Database, lineage.Handle, ShapeSnapshots.PageOffset(snapshot, afterId), false);
        var read = Join(await flights.ReadAsync(request, NotParked, Read, cancellationToken).ConfigureAwait(false));
        return await RecheckAsync(store, scope, lineage, read, cancellationToken).ConfigureAwait(false);

        async ValueTask<ShapeRead> Read(IDatabaseStore current, CancellationToken token)
        {
            return Detach(await ReadPageAtAsync(current, scope, lineage, snapshot, afterId, token).ConfigureAwait(false));
        }
    }
    private async ValueTask<ShapeRead> ReadPageAtAsync(IDatabaseStore store, ShapeScope scope, ShapeLineage lineage, long snapshot, string? afterId, CancellationToken cancellationToken)
    {
        try
        {
            if (await CurrentAsync(store, scope, lineage, cancellationToken).ConfigureAwait(false) is { } stale)
            {
                return stale;
            }
            if (await retention.CheckOffsetAsync(store, lineage.Handle, snapshot, cancellationToken).ConfigureAwait(false) is { } below)
            {
                return new MustRefetch(below.Message);
            }
            var frontier = await sealer.FrontierAsync(store, cancellationToken).ConfigureAwait(false);
            if (await snapshots.ReadPageAsync(store, lineage.Handle, snapshot, afterId, cancellationToken).ConfigureAwait(false) is not { } page)
            {
                if (snapshot > frontier && snapshot <= await ReadHeadAsync(store, cancellationToken).ConfigureAwait(false))
                {
                    return new Unavailable("the snapshot is above the local committed head; retry later");
                }

                return new MustRefetch("the snapshot page is not sealed for this handle");
            }

            try
            {
                if (await retention.CheckOffsetAsync(store, lineage.Handle, snapshot, cancellationToken).ConfigureAwait(false) is { } raised)
                {
                    page.Dispose();
                    return new MustRefetch(raised.Message);
                }

                if (await CurrentAsync(store, scope, lineage, cancellationToken).ConfigureAwait(false) is { } changed)
                {
                    page.Dispose();
                    return changed;
                }
                var next = page.NextId is null ? DocumentRoute.Decimal(snapshot) : ShapeSnapshots.PageOffset(snapshot, page.NextId);
                return new ShapeBody(page.Body, next, false, page);
            }
            catch
            {
                page.Dispose();
                throw;
            }
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
        if (await engine.OpenDatabaseAsync(scope.Database, cancellationToken).ConfigureAwait(false) is not { } store)
        {
            return UnknownDatabase;
        }

        var request = new ShapeFlightRequest(scope.Database, lineage.Handle, DocumentRoute.Decimal(offset), false);
        var read = Join(await flights.ReadAsync(request, NotParked, Read, cancellationToken).ConfigureAwait(false));
        return await RecheckAsync(store, scope, lineage, read, cancellationToken).ConfigureAwait(false);

        async ValueTask<ShapeRead> Read(IDatabaseStore current, CancellationToken token)
        {
            return Detach(await ReadAtAsync(current, scope, lineage, offset, fresh: false, token).ConfigureAwait(false));
        }
    }
    public async ValueTask<ShapeRead> WaitAsync(ShapeScope scope, ShapeLineage lineage, long offset, CancellationToken cancellationToken)
    {
        if (await engine.OpenDatabaseAsync(scope.Database, cancellationToken).ConfigureAwait(false) is not { } store)
        {
            return UnknownDatabase;
        }

        var key = new GateKey(scope.Database.OrganizationId, scope.Database.ProjectId, scope.Database.DatabaseId, lineage.Handle, offset);
        var group = gate.Enter(key);
        var parked = false;
        var admitted = false;
        try
        {
            var request = new ShapeFlightRequest(scope.Database, lineage.Handle, DocumentRoute.Decimal(offset), true, () =>
            {
                admitted = true;
                gate.Admit(group);
            });
            var read = Join(await flights.ReadAsync(request, Probe, Read, cancellationToken).ConfigureAwait(false));
            return await RecheckAsync(store, scope, lineage, read, cancellationToken).ConfigureAwait(false);

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
                    registry.Advance(scope.Database, scope.Collection.Name, scope.CanonicalFilter, offset);
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
    public ValueTask<ResumeResult> ResolveAsync(ShapeScope scope, ShapeLineage lineage, ReadOnlyMemory<byte> scope16, FeedCursor cursor, CancellationToken cancellationToken) =>
        ValueTask.FromResult<ResumeResult>(new MustRefetch("resume prefix cannot be proven"));
    private async ValueTask<ShapeRead> ReadAtAsync(IDatabaseStore store, ShapeScope scope, ShapeLineage lineage, long offset, bool fresh, CancellationToken cancellationToken)
    {
        try
        {
            if (await CurrentAsync(store, scope, lineage, cancellationToken).ConfigureAwait(false) is { } stale)
            {
                return stale;
            }

            var definition = Definition(store, scope, lineage);
            var frontier = await sealer.FrontierAsync(store, cancellationToken).ConfigureAwait(false);
            var head = await ReadHeadAsync(store, cancellationToken).ConfigureAwait(false);
            if (offset > head)
            {
                return new MustRefetch("the offset is above the sealed head");
            }

            if (offset > frontier)
            {
                return new Unavailable("the offset is above the local committed head; retry later", "unavailable");
            }

            if (await retention.CheckOffsetAsync(store, lineage.Handle, offset, cancellationToken).ConfigureAwait(false) is { } below)
            {
                return new MustRefetch(below.Message);
            }

            var read = fresh || offset == frontier
                ? await FreshAsync(store, definition, offset, cancellationToken).ConfigureAwait(false)
                : await ClosedAsync(store, definition, offset, cancellationToken).ConfigureAwait(false);
            if (read is ShapeBody body)
            {
                try
                {
                    if (await retention.CheckOffsetAsync(store, lineage.Handle, offset, cancellationToken).ConfigureAwait(false) is { } raised)
                    {
                        body.Lease?.Dispose();
                        return new MustRefetch(raised.Message);
                    }
                    if (await CurrentAsync(store, scope, lineage, cancellationToken).ConfigureAwait(false) is { } changed)
                    {
                        body.Lease?.Dispose();
                        return changed;
                    }
                }
                catch
                {
                    body.Lease?.Dispose();
                    throw;
                }
            }

            return read;
        }
        catch (ShapeSealOffsetException)
        {
            return new MustRefetch("the offset is not a retained commit boundary");
        }
        catch (ShapeTooLargeException)
        {
            return new Unavailable("a materialized commit exceeds 8 MiB", "shape_too_large");
        }
        catch (ObjectDisposedException)
        {
            return new Unavailable("the database is closing; retry later", "unavailable");
        }
        catch (Exception error) when (error is StorageBusyException or QosMemoryException)
        {
            return Busy;
        }
    }
    private async ValueTask<ShapeRead> RecheckAsync(IDatabaseStore store, ShapeScope scope, ShapeLineage lineage, ShapeRead read,
        CancellationToken cancellationToken)
    {
        if (scope.Filter.Access is null && !CollectionRuleAccess.EndUser)
        {
            return read;
        }

        try
        {
            if (await CurrentAsync(store, scope, lineage, cancellationToken).ConfigureAwait(false) is { } changed)
            {
                if (read is ShapeBody body)
                {
                    body.Lease?.Dispose();
                }
                return changed;
            }
            if (read is ShapeBody retained && scope.Filter.Access?.Owner is { } owner)
            {
                await using var transaction = await storage.BeginTransactionAsync(store, write: false, cancellationToken).ConfigureAwait(false);
                var authorization = transaction as IShapeOwnerAuthorization
                    ?? throw new NotSupportedException("the storage transaction does not support shape ownership checks");
                if (!await authorization.AuthorizeShapeBodyAsync(scope.Collection.Name, owner, retained.Json, cancellationToken).ConfigureAwait(false))
                {
                    retained.Lease?.Dispose();
                    return ShapeAccess.Denied;
                }
            }
            return read;
        }
        catch
        {
            if (read is ShapeBody body)
            {
                body.Lease?.Dispose();
            }
            throw;
        }
    }
    private async ValueTask<ShapeRead?> CurrentAsync(IDatabaseStore store, ShapeScope scope, ShapeLineage lineage, CancellationToken cancellationToken)
    {
        var resolved = await documents.GetCollectionAsync(scope.Database, scope.Collection.Name, cancellationToken).ConfigureAwait(false);
        if (resolved is not CollectionInfo current)
        {
            return resolved switch
            {
                NotFound missing => missing,
                Unavailable unavailable => unavailable,
                Conflict conflict => new Unavailable(conflict.Message),
                _ => throw new System.Diagnostics.UnreachableException(),
            };
        }

        var canonical = scope.CanonicalFilter;
        if (scope.Filter.Access is { } access)
        {
            var bound = await ShapeAccess.BindAsync(engine, scope, access.Identity, cancellationToken).ConfigureAwait(false);
            if (bound is not ShapeScope authorized)
            {
                return bound switch
                {
                    Forbidden denied => denied,
                    NotFound missing => missing,
                    Unavailable unavailable => unavailable,
                    ShapeScope => throw new System.Diagnostics.UnreachableException(),
                };
            }

            current = authorized.Collection;
            canonical = authorized.CanonicalFilter;
        }
        else if (CollectionRuleAccess.EndUser)
        {
            return ShapeAccess.Denied;
        }

        var identity = ShapeHandle.ComputeCanonical(store, current, canonical);
        return identity.Handle == lineage.Handle ? (ShapeRead?)null : new MustRefetch("the shape lineage changed", new ShapeLineage(identity.ShapeId, identity.Handle));
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

        var body = new ShapeBody(chunk.Body, DocumentRoute.Decimal(chunk.EndOffset), false, chunk);
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

        if (fresh is null)
        {
            return new MustRefetch("the offset is not a retained commit boundary");
        }

        return fresh;
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
                    if (await CurrentAsync(store, scope, lineage, stop.Token).ConfigureAwait(false) is null)
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
                var current = await documents.GetCollectionAsync(scope.Database, retired.Collection, cancellationToken).ConfigureAwait(false);
                if (current is CollectionInfo collection
                    && ShapeHandle.ComputeCanonical(store, collection, retired.Canonical).Handle == retired.Handle)
                {
                    return;
                }
                if (current is not CollectionInfo and not NotFound)
                {
                    registry.Retire(retired);
                    requeued = true;
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

        if (registry.ClaimTrim(scope.Database, scope.Collection.Name, scope.CanonicalFilter))
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
    private static ShapeSealDefinition Definition(IDatabaseStore store, ShapeScope scope, ShapeLineage lineage) =>
        new(lineage.ShapeId, lineage.Handle, scope.Collection.Name, scope.Collection.Id, ResumeScope(scope.Database, scope.Collection, scope.CanonicalFilter),
            store.Incarnation, Predicate(scope), scope.Filter.Access?.Owner);

    private static ShapeSnapshotDefinition SnapshotDefinition(IDatabaseStore store, ShapeScope scope, ShapeLineage lineage) =>
        new(lineage.Handle, lineage.ShapeId, scope.Collection.Name, scope.Collection.Id, ResumeScope(scope.Database, scope.Collection, scope.CanonicalFilter),
            store.Incarnation, Predicate(scope), scope.Filter.Access is { Owner: not null } access ? access.Rules.OwnerFilter(access.Identity.Actor) : null);

    private static Func<StoredDocument, bool>? Predicate(ShapeScope scope) => scope.Filter.Filter is null && scope.Filter.Access?.Owner is null ? null : scope.Filter.Matches;
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

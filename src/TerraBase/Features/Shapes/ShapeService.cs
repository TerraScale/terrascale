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

/// <summary>
/// The shape reads over the sealing, snapshot, flight, retention and filter components (shape-reads.md sections 3 to 9).
/// Serves plaintext T0 only: nothing here reads the data keys, and sealed objects reach the mirror through the FR-22 hooks.
/// </summary>
internal sealed class ShapeService(
    IStorageEngine engine, LocalReplica replica, IDocumentService documents, IShapeSealer sealer, IShapeSnapshotSealer snapshots,
    IShapeStorage storage, IShapeRetention retention, IShapeFilterRegistry filters, ShapeRegistry registry, ShapeLiveGate gate,
    IShapeFlights<ShapeRead> flights, IMemoryBudget memory, IHostApplicationLifetime lifetime, ILogger<ShapeService> logger) : IShapeLineages, IShapeSnapshots, IShapeChunks, IShapeLiveTail, IShapeResume
{
    private int _maintaining;
    private static readonly QuotaExceeded Busy = new("shape reads are at capacity; retry later", "shape_busy");
    private static readonly NotFound UnknownDatabase = new("the database does not exist", "unknown_database");

    /// <summary>The resume scope of section 9: the same on every machine, so a cursor from one machine names this shape.</summary>
    internal static byte[] ResumeScope(TenantContext tenant, CollectionInfo collection, string canonical)
    {
        var fields = string.Join('\0', tenant.OrganizationId, tenant.ProjectId, tenant.DatabaseId, collection.Name, collection.Id, canonical, "1");
        return SHA256.HashData(Encoding.UTF8.GetBytes("TB-SHAPE-RESUME-V1\0" + fields)).AsSpan(0, 16).ToArray();
    }

    /// <inheritdoc />
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

        var identity = ShapeHandle.ComputeCanonical(store, replica.Id, scope.Collection, scope.CanonicalFilter);
        var lineage = new ShapeLineage(identity.ShapeId, identity.Handle);
        var collection = scope.Collection.Name;
        if (scope.Filter.Filter is not null)
        {
            // A bootstrap admits the filter; a continuation must still be admitted, or it refetches (section 8).
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
                NotFound missing => missing,
                Unavailable unavailable => unavailable,
                _ => throw new System.Diagnostics.UnreachableException(),
            };
        }

        if (registry.CurrentHandle(scope.Database, collection, scope.CanonicalFilter) != lineage.Handle)
        {
            var head = await ReadHeadAsync(store, cancellationToken).ConfigureAwait(false);
            if (registry.Track(scope.Database, collection, scope.Filter, Definition(scope, lineage), head) is { } full)
            {
                return full;
            }
        }

        ScheduleMaintenance(scope, store, lineage);
        return lineage;
    }

    /// <inheritdoc />
    /// <remarks>A filtered handle releases keys only while its registry entry and filter admission both hold, and its fresh identity matches.</remarks>
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

        var unfiltered = ShapeHandle.ComputeCanonical(store, replica.Id, collection, string.Empty);
        var current = new ShapeLineage(unfiltered.ShapeId, unfiltered.Handle);
        if (registry.FilteredShape(database, collection.Name, handle) is not { } filter)
        {
            return current;
        }

        // Eviction removes the registry entry, and the filter registry must still admit the filter before any key is released.
        if (filters.Check(database, collection.Name, filter) is not null)
        {
            return new Evicted(current);
        }

        var filtered = ShapeHandle.ComputeCanonical(store, replica.Id, collection, filter.Canonical);
        return filtered.Handle == handle ? new ShapeLineage(filtered.ShapeId, filtered.Handle) : current;
    }

    /// <inheritdoc />
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
                    MustRefetch changed => changed,
                    NotFound missing => missing,
                    Unavailable unavailable => unavailable,
                    _ => throw new System.Diagnostics.UnreachableException(),
                };
            }

            // Seals at the head when none is due or the newest is below the floor (ShapeSnapshotDueAsync).
            var snapshot = await snapshots.SealIfDueAsync(store, SnapshotDefinition(scope, lineage), cancellationToken).ConfigureAwait(false);
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

    /// <inheritdoc />
    public async ValueTask<ShapeRead> PageAsync(ShapeScope scope, ShapeLineage lineage, long snapshot, string? afterId, CancellationToken cancellationToken)
    {
        if (await engine.OpenDatabaseAsync(scope.Database, cancellationToken).ConfigureAwait(false) is not { } store)
        {
            return UnknownDatabase;
        }

        try
        {
            if (await CurrentAsync(store, scope, lineage, cancellationToken).ConfigureAwait(false) is { } stale)
            {
                return stale;
            }

            // Retained snapshots below the floor are kept for the bootstrap's reseal, never served by URL (section 8).
            if (await retention.CheckOffsetAsync(store, lineage.Handle, snapshot, cancellationToken).ConfigureAwait(false) is { } below)
            {
                return new MustRefetch(below.Message);
            }

            if (await snapshots.ReadPageAsync(store, lineage.Handle, snapshot, afterId, cancellationToken).ConfigureAwait(false) is not { } page)
            {
                return new MustRefetch("the snapshot page is not sealed for this handle");
            }

            try
            {
                // The floor is raised before its deletes, so a page that raced a trim is refused once its bytes are in hand.
                if (await retention.CheckOffsetAsync(store, lineage.Handle, snapshot, cancellationToken).ConfigureAwait(false) is { } raised)
                {
                    page.Dispose();
                    return new MustRefetch(raised.Message);
                }

                // The last page's next_offset is the decimal snapshot offset (section 3.2).
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

    /// <inheritdoc />
    /// <remarks>Non-live reads join one flight per (handle, offset), so concurrent identical reads share one payload read (section 4).</remarks>
    public async ValueTask<ShapeRead> ReadAsync(ShapeScope scope, ShapeLineage lineage, long offset, CancellationToken cancellationToken)
    {
        if (await engine.OpenDatabaseAsync(scope.Database, cancellationToken).ConfigureAwait(false) is not { } store)
        {
            return UnknownDatabase;
        }

        var request = new ShapeFlightRequest(scope.Database, lineage.Handle, DocumentRoute.Decimal(offset), false, null);
        return Join(await flights.ReadAsync(request, NotParked, Read, cancellationToken).ConfigureAwait(false));

        // A non-live flight never probes or waits.
        static ValueTask<bool> NotParked(IDatabaseStore current, CancellationToken token)
        {
            return ValueTask.FromResult(false);
        }

        async ValueTask<ShapeRead> Read(IDatabaseStore current, CancellationToken token)
        {
            return Detach(await ReadAtAsync(current, scope, lineage, offset, fresh: false, token).ConfigureAwait(false));
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Probe and read run in the flight's starter only. The probe runs after the flight captured the commit count, and it parks
    /// the gate only when the offset is the head. Only a parked flight answers fresh: a request behind the head gets the closed chunk.
    /// The read revalidates the lineage after the wake, so a re-created collection refetches.
    /// </remarks>
    public async ValueTask<ShapeRead> WaitAsync(ShapeScope scope, ShapeLineage lineage, long offset, long? cursor, CancellationToken cancellationToken)
    {
        if (await engine.OpenDatabaseAsync(scope.Database, cancellationToken).ConfigureAwait(false) is not { } store)
        {
            return UnknownDatabase;
        }

        var key = new GateKey(scope.Database.OrganizationId, scope.Database.ProjectId, scope.Database.DatabaseId, lineage.Handle, offset, cursor);
        var group = gate.Enter(key);
        var parked = false;
        var admitted = false;
        try
        {
            var request = new ShapeFlightRequest(scope.Database, lineage.Handle, DocumentRoute.Decimal(offset), true, cursor, () =>
            {
                admitted = true;
                gate.Admit(group);
            });
            return Join(await flights.ReadAsync(request, Probe, Read, cancellationToken).ConfigureAwait(false));

            async ValueTask<bool> Probe(IDatabaseStore current, CancellationToken token)
            {
                try
                {
                    parked = await ReadHeadAsync(current, token).ConfigureAwait(false) == offset;
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

    /// <inheritdoc />
    public ValueTask<ResumeResult> ResolveAsync(ShapeScope scope, ShapeLineage lineage, ReadOnlyMemory<byte> scope16, FeedCursor cursor, CancellationToken cancellationToken) =>
        // Planned gate (section 9): no resume proof is persisted yet, so every resume is refused.
        ValueTask.FromResult<ResumeResult>(new MustRefetch("resume prefix cannot be proven"));

    // One read at offset: a closed chunk, or a fresh body at the head or, when fresh, behind it (section 4).
    private async ValueTask<ShapeRead> ReadAtAsync(IDatabaseStore store, ShapeScope scope, ShapeLineage lineage, long offset, bool fresh, CancellationToken cancellationToken)
    {
        try
        {
            if (await CurrentAsync(store, scope, lineage, cancellationToken).ConfigureAwait(false) is { } stale)
            {
                return stale;
            }

            var definition = Definition(scope, lineage);
            var head = await ReadHeadAsync(store, cancellationToken).ConfigureAwait(false);
            if (offset > head)
            {
                return new MustRefetch("the offset is above the sealed head");
            }

            if (await retention.CheckOffsetAsync(store, lineage.Handle, offset, cancellationToken).ConfigureAwait(false) is { } below)
            {
                return new MustRefetch(below.Message);
            }

            var read = fresh || offset == head
                ? await FreshAsync(store, definition, offset, cancellationToken).ConfigureAwait(false)
                : await ClosedAsync(store, definition, offset, cancellationToken).ConfigureAwait(false);
            // The floor is raised before its deletes, so a read that raced a trim is refused after its bytes are in hand.
            if (read is ShapeBody body)
            {
                try
                {
                    if (await retention.CheckOffsetAsync(store, lineage.Handle, offset, cancellationToken).ConfigureAwait(false) is { } raised)
                    {
                        body.Lease?.Dispose();
                        return new MustRefetch(raised.Message);
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

    // A lineage is current only while its collection is the one the request named: a re-created collection changes the handle.
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

        var identity = ShapeHandle.ComputeCanonical(store, replica.Id, current, scope.CanonicalFilter);
        return identity.Handle == lineage.Handle ? (ShapeRead?)null : new MustRefetch("the shape lineage changed", new ShapeLineage(identity.ShapeId, identity.Handle));
    }

    // The sealed chunk at offset, sealed now when it is not yet. Its bytes are immutable and keep their admission until sent.
    private async ValueTask<ShapeRead> ClosedAsync(IDatabaseStore store, ShapeSealDefinition definition, long offset, CancellationToken cancellationToken)
    {
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

        return new ShapeBody(chunk.Body, DocumentRoute.Decimal(chunk.EndOffset), false, chunk);
    }

    // A fresh body at offset: built from the sealed chunk's payload in one transaction, sealing the chunk first when needed.
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

    // The flight's answer is shared, so each copy is reserved under the flight's result memory before it is allocated, and the
    // chunk's own admission is released once it is copied.
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

    // The requester holds the flight itself: its admission stays until the response is sent, and the flight disposes it then.
    private static ShapeRead Hold(ShapeFlight<ShapeRead> flight)
    {
        if (flight.Value is not ShapeBody body)
        {
            flight.Dispose();
            return flight.Value;
        }

        return new ShapeBody(body.Json, body.NextOffset, body.UpToDate, flight);
    }

    private static ShapeRead Join(ShapeFlightResult<ShapeRead> result) => result switch
    {
        ShapeFlight<ShapeRead> flight => Hold(flight),
        QuotaExceeded refused => refused,
        NotFound => UnknownDatabase,
        Unavailable stuck => stuck,
    };

    // At most one bounded maintenance task runs per machine, under its database's admission and shutdown token.
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
                    // The next request retries maintenance; no request depends on this task.
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
                    && ShapeHandle.ComputeCanonical(store, replica.Id, collection, retired.Canonical).Handle == retired.Handle)
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
                // The next interval's claim retries the pass.
            }
        }
    }

    private static ShapeSealDefinition Definition(ShapeScope scope, ShapeLineage lineage) =>
        new(lineage.ShapeId, lineage.Handle, scope.Collection.Name, scope.Collection.Id, ResumeScope(scope.Database, scope.Collection, scope.CanonicalFilter), Predicate(scope));

    private static ShapeSnapshotDefinition SnapshotDefinition(ShapeScope scope, ShapeLineage lineage) =>
        new(lineage.Handle, lineage.ShapeId, scope.Collection.Name, scope.Collection.Id, ResumeScope(scope.Database, scope.Collection, scope.CanonicalFilter), Predicate(scope));

    private static Func<StoredDocument, bool>? Predicate(ShapeScope scope) => scope.Filter.Filter is null ? null : scope.Filter.Matches;
}

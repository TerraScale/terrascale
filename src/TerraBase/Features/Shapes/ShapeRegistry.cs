using TerraBase.Engine;
using TerraBase.Errors;
using TerraBase.Features.Memory;
using TerraBase.Features.Qos;
using TerraBase.Tenancy;
using ZLogger;

namespace TerraBase.Features.Shapes;

/// <summary>
/// The shapes this machine serves, so the flight sealer can seal every one up to the head on wake (free-reads.md 3.6).
/// An LRU cache, not an admission gate: at most <see cref="MaxShapes"/> entries, and <see cref="MaxShapesPerDatabase"/> per database,
/// evict the least recently used entry. Eviction only stops wake sealing; the handle stays valid and reads seal on demand.
/// Each entry's bytes are reserved from <see cref="IMemoryBudget"/> before it is allocated and released when it leaves.
/// Replaced handles are queued for retirement and dropped by <see cref="TakeRetired"/>.
/// </summary>
internal sealed class ShapeRegistry(IShapeSealer sealer, IShapeStorage storage, IMemoryBudget budget, TimeProvider time, ILogger<ShapeRegistry>? logger = null)
    : IShapeFlightSealer, IDisposable
{
    internal const int MaxShapes = 1024;
    internal const int MaxShapesPerDatabase = 256;
    // shortcut: restart, overflow, eviction and unobserved collection drops leave derived bytes; add a durable database-wide retirement sweep in FR-16.
    internal const int MaxRetired = 1024;
    internal static readonly TimeSpan TrimInterval = TimeSpan.FromMinutes(1);

    // The entry, its list nodes, the key record and their dictionary slots, beyond the key strings.
    private const int EntryOverhead = 512;

    private readonly Lock _gate = new();
    private readonly Dictionary<RegistryKey, Entry> _entries = [];
    private readonly LinkedList<Entry> _lru = new();
    private readonly Dictionary<ShapeFilterKey, RegistryKey> _filtered = [];
    private readonly Dictionary<DatabaseKey, int> _counts = [];
    private readonly LinkedList<RetiredShape> _retired = new();

    /// <summary>The handle this machine currently serves for the shape, or null when it is not registered. Touches the entry.</summary>
    public string? CurrentHandle(TenantContext database, string collection, string canonical)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(new RegistryKey(database, collection, canonical), out var entry))
            {
                return null;
            }

            Touch(entry);
            return entry.Handle;
        }
    }

    /// <summary>The filter of the filtered shape this machine currently serves under the handle, or null.</summary>
    // shortcut: a linear scan of at most MaxShapes entries per key request, index by handle if key requests dominate.
    public ShapeFilter? FilteredShape(TenantContext database, string collection, string handle)
    {
        var owner = DatabaseKey.Of(database);
        lock (_gate)
        {
            foreach (var (key, entry) in _entries)
            {
                if (entry.Handle == handle && key.Database == owner && key.Collection == collection && entry.Filter.Filter is not null)
                {
                    return entry.Filter;
                }
            }

            return null;
        }
    }

    /// <summary>
    /// Registers a shape at its head. A different handle for the same key replaces the entry and queues the old handle for retirement.
    /// Evicts the least recently used entry of the database, then of the machine, when full. Null, or <see cref="QuotaExceeded"/>
    /// when the memory budget cannot hold the entry; nothing changes in that case.
    /// </summary>
    public QuotaExceeded? Track(TenantContext database, string collection, ShapeFilter filter, ShapeSealDefinition definition, long head)
    {
        var key = new RegistryKey(database, collection, filter.Canonical);
        lock (_gate)
        {
            var existing = _entries.GetValueOrDefault(key);
            if (existing?.Handle == definition.Handle)
            {
                Touch(existing);
                return null;
            }

            var bytes = EntryBytes(database, collection, filter, definition.Handle);
            if (budget.TryReserve(MemoryLane.Foreground, bytes, database) is not { } reservation)
            {
                return new QuotaExceeded("the memory budget cannot hold another shape; retry later", "shape_busy");
            }

            if (existing is not null)
            {
                Remove(existing, release: false);
                Enqueue(new RetiredShape(existing.Database, collection, filter.Canonical, existing.Handle, existing.Reservation));
            }
            else
            {
                if (_counts.GetValueOrDefault(key.Database) >= MaxShapesPerDatabase)
                {
                    Remove(OldestOf(key.Database));
                }

                if (_entries.Count >= MaxShapes)
                {
                    Remove(_lru.Last!.Value);
                }
            }

            var entry = new Entry(key, database, definition.Handle, filter, definition, head, reservation);
            entry.Node = _lru.AddFirst(entry);
            _entries[key] = entry;
            _counts[key.Database] = _counts.GetValueOrDefault(key.Database) + 1;
            if (filter.Filter is not null)
            {
                _filtered[new ShapeFilterKey(database.DatabaseId!, collection, filter.Canonical)] = key;
            }

            return null;
        }
    }

    /// <summary>
    /// Raises the shape's sealed cursor to a commit boundary a parked reader reached. Monotonic: a lower offset changes nothing,
    /// and an unregistered shape is a no-op.
    /// </summary>
    public void Advance(TenantContext database, string collection, string canonical, long offset)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(new RegistryKey(database, collection, canonical), out var entry))
            {
                entry.Cursor = Math.Max(entry.Cursor, offset);
            }
        }
    }

    /// <summary>Forgets the shapes the filter registry evicted, so the flight sealer stops sealing them. Their handles keep reading on demand.</summary>
    public void Forget(IReadOnlyList<ShapeFilterKey> evicted)
    {
        lock (_gate)
        {
            foreach (var filtered in evicted)
            {
                if (_filtered.TryGetValue(filtered, out var key) && _entries.TryGetValue(key, out var entry))
                {
                    Remove(entry);
                }
            }
        }
    }

    /// <summary>True when a retention pass of this shape is due; claims the slot so one pass runs per interval.</summary>
    public bool ClaimTrim(TenantContext database, string collection, string canonical)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(new RegistryKey(database, collection, canonical), out var entry))
            {
                return false;
            }

            var now = time.GetUtcNow();
            if (now - entry.LastTrim < TrimInterval)
            {
                return false;
            }

            entry.LastTrim = now;
            return true;
        }
    }

    /// <summary>Returns one retired handle from this database; the caller revalidates its authoritative lineage before dropping it.</summary>
    public RetiredShape? TakeRetired(TenantContext database)
    {
        lock (_gate)
        {
            for (var node = _retired.First; node is not null; node = node.Next)
            {
                if (DatabaseKey.Of(node.Value.Database) == DatabaseKey.Of(database))
                {
                    var retired = node.Value;
                    _retired.Remove(node);
                    return retired;
                }
            }
            return null;
        }
    }

    /// <summary>Puts back a retired handle whose drop did not finish, or whose batch was busy. Keeps its key, so the next take can drop it.</summary>
    public void Retire(RetiredShape retired)
    {
        lock (_gate)
        {
            Enqueue(retired);
        }
    }

    // Each shape of the database seals from its own cursor, so a wake seals only the commits it has not sealed yet.
    // Best effort per shape: one shape's failure never stops the others, and this method never reports a shape's error as the database's.
    public async ValueTask<ShapeFlightSealResult> SealAsync(IDatabaseStore store, CancellationToken cancellationToken)
    {
        if (store.Tenant is not { } tenant)
        {
            return new Done();
        }

        using var enumeration = budget.TryReserve(MemoryLane.Bulk, MaxShapesPerDatabase * 16L + 512, tenant);
        if (enumeration is null)
        {
            return new Done();
        }
        long head;
        try
        {
            await using var transaction = await storage.BeginTransactionAsync(store, write: false, cancellationToken).ConfigureAwait(false);
            head = await ((IShapeFreshStorage)transaction).ReadShapeHeadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (!cancellationToken.IsCancellationRequested
            && error is ShapeSealOffsetException or ObjectDisposedException or StorageBusyException or QosMemoryException)
        {
            // Each requester reopens and reports its own missing, closing or busy database.
            logger?.ZLogWarning(error, $"Shape wake seal skipped a changing database");
            return new Done();
        }
        List<Entry> shapes;
        lock (_gate)
        {
            shapes = [.. _lru.Where(entry => entry.Key.Database == DatabaseKey.Of(tenant))];
        }

        foreach (var entry in shapes)
        {
            long from;
            lock (_gate)
            {
                from = entry.Cursor;
            }
            if (from >= head)
            {
                continue;
            }

            try
            {
                var to = await sealer.SealUpToHeadAsync(store, entry.Definition, from, cancellationToken).ConfigureAwait(false);
                lock (_gate)
                {
                    entry.Cursor = Math.Max(entry.Cursor, to);
                }
            }
            catch (ShapeSealOffsetException) when (!cancellationToken.IsCancellationRequested)
            {
                // The cursor is no longer a retained boundary (retention, a reopen or a re-created collection). Forget the entry; the next resolve tracks it at the head.
                lock (_gate)
                {
                    if (_entries.TryGetValue(entry.Key, out var current) && ReferenceEquals(current, entry))
                    {
                        Remove(entry);
                    }
                }
            }
            catch (Exception error) when (!cancellationToken.IsCancellationRequested)
            {
                // This shape's own read reports the same error (ReadAtAsync); the other shapes still seal.
                logger?.ZLogWarning(error, $"Shape wake seal skipped a shape");
            }
        }

        return new Done();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var entry in _entries.Values)
            {
                entry.Reservation.Dispose();
            }

            _entries.Clear();
            _lru.Clear();
            _filtered.Clear();
            _counts.Clear();
            foreach (var retired in _retired)
            {
                retired.Dispose();
            }
            _retired.Clear();
        }
    }

    private void Touch(Entry entry)
    {
        _lru.Remove(entry.Node);
        _lru.AddFirst(entry.Node);
    }

    // Unlinks every index of the entry and returns its bytes. The caller holds the gate and has checked the entry is current.
    private void Remove(Entry entry, bool release = true)
    {
        _lru.Remove(entry.Node);
        _entries.Remove(entry.Key);
        if (entry.Filter.Filter is not null)
        {
            _filtered.Remove(new ShapeFilterKey(entry.Key.Database.Database!, entry.Key.Collection, entry.Key.Canonical));
        }

        if (--_counts[entry.Key.Database] == 0)
        {
            _counts.Remove(entry.Key.Database);
        }

        if (release)
        {
            entry.Reservation.Dispose();
        }
    }

    private Entry OldestOf(DatabaseKey database)
    {
        var node = _lru.Last;
        while (node is not null && node.Value.Key.Database != database)
        {
            node = node.Previous;
        }

        return node?.Value ?? throw new InvalidOperationException("a full database holds an entry");
    }

    // shortcut: overflow drops the oldest retired handle, see MaxRetired.
    private void Enqueue(RetiredShape retired)
    {
        if (_retired.Count == MaxRetired)
        {
            _retired.First!.Value.Dispose();
            _retired.RemoveFirst();
        }

        _retired.AddLast(retired);
    }

    private static long EntryBytes(TenantContext database, string collection, ShapeFilter filter, string handle) =>
        EntryOverhead + 2L * (Length(database.OrganizationId) + Length(database.ProjectId) + Length(database.DatabaseId)
            + collection.Length + filter.Canonical.Length + handle.Length);

    private static int Length(string? value) => value?.Length ?? 0;

    private readonly record struct DatabaseKey(string? Organization, string? Project, string? Database)
    {
        public static DatabaseKey Of(TenantContext tenant) => new(tenant.OrganizationId, tenant.ProjectId, tenant.DatabaseId);
    }

    private readonly record struct RegistryKey(DatabaseKey Database, string Collection, string Canonical)
    {
        public RegistryKey(TenantContext database, string collection, string canonical)
            : this(DatabaseKey.Of(database), collection, canonical)
        {
        }
    }

    private sealed class Entry(RegistryKey key, TenantContext database, string handle, ShapeFilter filter, ShapeSealDefinition definition,
        long cursor, IMemoryReservation reservation)
    {
        public RegistryKey Key { get; } = key;

        public TenantContext Database { get; } = database;

        public string Handle { get; } = handle;

        public ShapeFilter Filter { get; } = filter;

        public ShapeSealDefinition Definition { get; } = definition;

        public IMemoryReservation Reservation { get; } = reservation;

        public LinkedListNode<Entry> Node { get; set; } = null!;

        // The commit boundary sealed through; a wake seals from here.
        public long Cursor { get; set; } = cursor;

        public DateTimeOffset LastTrim { get; set; } = DateTimeOffset.MinValue;
    }
}

/// <summary>
/// A handle this machine replaced, with the key it belongs to, so a failed or partial drop can be retried by <see cref="ShapeRegistry.Retire"/>.
/// </summary>
internal sealed record RetiredShape(TenantContext Database, string Collection, string Canonical, string Handle, IMemoryReservation Reservation) : IDisposable
{
    public void Dispose() => Reservation.Dispose();
}

/// <summary>
/// The requesters parked on one live flight, for <c>terrabase.shape.requests.waiting</c>. A requester counts only once its
/// flight's probe has parked at the head: the probe runs after the flight captured the commit count, so a write issued after the
/// gauge reaches N always wakes those N, and never races the probe.
/// </summary>
internal sealed class ShapeLiveGate(IShapeMetrics metrics)
{
    private readonly Lock _gate = new();
    private readonly Dictionary<GateKey, Group> _groups = [];

    public Group Enter(GateKey key)
    {
        lock (_gate)
        {
            if (!_groups.TryGetValue(key, out var group))
            {
                _groups[key] = group = new Group();
            }

            group.Requesters++;
            Reconcile(group);
            return group;
        }
    }

    public void Park(Group group)
    {
        lock (_gate)
        {
            group.Parked = true;
            Reconcile(group);
        }
    }

    public void Admit(Group group)
    {
        lock (_gate)
        {
            group.Admitted++;
            Reconcile(group);
        }
    }

    public void Unpark(Group group)
    {
        lock (_gate)
        {
            group.Parked = false;
            Reconcile(group);
        }
    }

    public void Leave(GateKey key, Group group, bool admitted)
    {
        lock (_gate)
        {
            group.Requesters--;
            if (admitted)
            {
                group.Admitted--;
            }
            if (group.Requesters == 0 && _groups.GetValueOrDefault(key) == group)
            {
                _groups.Remove(key);
            }

            Reconcile(group);
        }
    }

    // The gauge is the parked requesters of every group, so each change posts only its difference.
    private void Reconcile(Group group)
    {
        var reported = group.Parked ? group.Admitted : 0;
        if (reported != group.Reported)
        {
            metrics.ChangeWaiting(reported - group.Reported);
            group.Reported = reported;
        }
    }

    internal sealed class Group
    {
        public long Requesters { get; set; }

        public long Admitted { get; set; }

        public bool Parked { get; set; }

        public long Reported { get; set; }
    }
}

/// <summary>A live flight's identity from the request: the database, handle, offset and bucket.</summary>
internal readonly record struct GateKey(string? Organization, string? Project, string? Database, string Handle, long Offset, long? Cursor);

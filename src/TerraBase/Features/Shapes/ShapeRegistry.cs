using TerraBase.Engine;
using TerraBase.Errors;
using TerraBase.Features.Memory;
using TerraBase.Features.Qos;
using TerraBase.Tenancy;
using ZLogger;

namespace TerraBase.Features.Shapes;
internal sealed class ShapeRegistry(IShapeSealer sealer, IShapeStorage storage, IMemoryBudget budget, TimeProvider time, ILogger<ShapeRegistry>? logger = null)
    : IShapeFlightSealer, IDisposable
{
    internal const int MaxShapes = 1024;
    internal const int MaxShapesPerDatabase = 256;
    internal const int MaxRetired = 1024;
    internal static readonly TimeSpan TrimInterval = TimeSpan.FromMinutes(1);
    private const int EntryOverhead = 512;

    private readonly Lock _gate = new();
    private readonly Dictionary<RegistryKey, Entry> _entries = [];
    private readonly LinkedList<Entry> _lru = new();
    private readonly Dictionary<ShapeFilterKey, RegistryKey> _filtered = [];
    private readonly Dictionary<DatabaseKey, int> _counts = [];
    private readonly LinkedList<RetiredShape> _retired = new();
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
    public ShapeFilter? FilteredShape(TenantContext database, string collection, string handle)
    {
        var owner = DatabaseKey.Of(database);
        lock (_gate)
        {
            foreach (var (key, entry) in _entries)
            {
                if (entry.Handle == handle && key.Database == owner && key.Collection == collection && entry.Filter.Canonical.Length > 0)
                {
                    return entry.Filter;
                }
            }

            return null;
        }
    }
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
            if (filter.Canonical.Length > 0)
            {
                _filtered[new ShapeFilterKey(database.DatabaseId!, collection, filter.Canonical)] = key;
            }

            return null;
        }
    }
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
    public void Retire(RetiredShape retired)
    {
        lock (_gate)
        {
            Enqueue(retired);
        }
    }
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
    private void Remove(Entry entry, bool release = true)
    {
        _lru.Remove(entry.Node);
        _entries.Remove(entry.Key);
        if (entry.Filter.Canonical.Length > 0)
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
        EntryOverhead + filter.AuthorizationBytes + 2L * (Length(database.OrganizationId) + Length(database.ProjectId) + Length(database.DatabaseId)
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
        public long Cursor { get; set; } = cursor;

        public DateTimeOffset LastTrim { get; set; } = DateTimeOffset.MinValue;
    }
}
internal sealed record RetiredShape(TenantContext Database, string Collection, string Canonical, string Handle, IMemoryReservation Reservation) : IDisposable
{
    public void Dispose() => Reservation.Dispose();
}
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
internal readonly record struct GateKey(string? Organization, string? Project, string? Database, string Handle, long Offset);

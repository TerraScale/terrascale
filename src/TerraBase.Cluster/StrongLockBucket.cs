using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace TerraBase.Cluster;

internal struct HolderSlot
{
    public StrongLockHolder? Single;
    public List<StrongLockHolder>? Many;

    public void Add(StrongLockHolder holder)
    {
        if (Many is not null)
        {
            Many.Add(holder);
            return;
        }

        if (Single is null)
        {
            Single = holder;
            return;
        }

        Many = [Single, holder];
        Single = null;
    }

    public bool Remove(StrongLockHolder holder)
    {
        if (Many is not null)
        {
            for (var index = 0; index < Many.Count; index++)
            {
                if (ReferenceEquals(Many[index], holder))
                {
                    var last = Many.Count - 1;
                    Many[index] = Many[last];
                    Many.RemoveAt(last);
                    break;
                }
            }

            if (Many.Count == 1)
            {
                Single = Many[0];
                Many = null;
                return false;
            }

            return Many.Count == 0;
        }

        if (ReferenceEquals(Single, holder))
        {
            Single = null;
            return true;
        }

        return Single is null;
    }
}

internal struct ConflictCollector
{
    private StrongLockHolder? _first;
    private List<StrongLockHolder>? _many;
    private HashSet<StrongLockHolder>? _seen;

    public StrongLockHolder? Oldest { get; private set; }

    public readonly bool HasConflicts => _first is not null;

    public void Add(StrongLockHolder holder)
    {
        if (_first is null)
        {
            _first = holder;
            Oldest = holder;
            return;
        }

        if (_many is null)
        {
            if (ReferenceEquals(_first, holder))
            {
                return;
            }

            _many = [_first, holder];
            if (Oldest is { } current && holder.Priority < current.Priority)
            {
                Oldest = holder;
            }

            return;
        }

        if (_seen is not null)
        {
            if (!_seen.Add(holder))
            {
                return;
            }
        }
        else if (_many.Count <= 4)
        {
            for (var index = 0; index < _many.Count; index++)
            {
                if (ReferenceEquals(_many[index], holder))
                {
                    return;
                }
            }
        }
        else
        {
            _seen = new HashSet<StrongLockHolder>(_many, ReferenceEqualityComparer.Instance);
            if (!_seen.Add(holder))
            {
                return;
            }
        }

        _many.Add(holder);
        if (Oldest is { } oldest && holder.Priority < oldest.Priority)
        {
            Oldest = holder;
        }
    }

    public void AddSlot(in HolderSlot slot)
    {
        if (slot.Single is { } single)
        {
            Add(single);
        }
        else if (slot.Many is { } many)
        {
            for (var index = 0; index < many.Count; index++)
            {
                Add(many[index]);
            }
        }
    }

    public readonly StrongLockHolder[] ToArray() =>
        _many is not null ? [.. _many] : _first is { } single ? [single] : [];
}

internal sealed class DatabaseLockBucket
{
    private readonly Dictionary<Ulid, int> _indexByTxId = [];
    private readonly List<StrongLockHolder> _holders = [];
    private readonly List<StrongLockHolder> _schemaHolders = [];
    private readonly Dictionary<RowKey, HolderSlot> _writeKeys = [];
    private readonly Dictionary<RowKey, HolderSlot> _readKeys = [];
    private readonly Dictionary<string, HolderSlot> _writeTables = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HolderSlot> _scannedTables = new(StringComparer.OrdinalIgnoreCase);

    public long NextExpiryTimestamp { get; set; } = long.MaxValue;

    public int Count => _holders.Count;

    public List<StrongLockHolder> Holders => _holders;

    public bool TryGet(Ulid txId, [NotNullWhen(true)] out StrongLockHolder? holder)
    {
        if (_indexByTxId.TryGetValue(txId, out var index))
        {
            holder = _holders[index];
            return true;
        }

        holder = null;
        return false;
    }

    public void Add(StrongLockHolder holder)
    {
        var index = _holders.Count;
        _holders.Add(holder);
        _indexByTxId[holder.TxId] = index;
        if (holder.ExpiresAtTimestamp < NextExpiryTimestamp)
        {
            NextExpiryTimestamp = holder.ExpiresAtTimestamp;
        }

        if (holder.IsSchemaChange)
        {
            _schemaHolders.Add(holder);
        }

        IndexSet(_writeKeys, holder.WriteKeys, holder);
        IndexSet(_readKeys, holder.ReadKeys, holder);
        IndexSet(_writeTables, holder.WriteTables, holder);
        IndexSet(_scannedTables, holder.ScannedTables, holder);
    }

    public bool Remove(Ulid txId)
    {
        if (!_indexByTxId.TryGetValue(txId, out var index))
        {
            return false;
        }

        RemoveAt(index);
        return true;
    }

    public void RemoveAt(int index)
    {
        var holder = _holders[index];
        var last = _holders.Count - 1;
        if (index != last)
        {
            var swapped = _holders[last];
            _holders[index] = swapped;
            _indexByTxId[swapped.TxId] = index;
        }

        _holders.RemoveAt(last);
        _ = _indexByTxId.Remove(holder.TxId);
        if (_holders.Count == 0)
        {
            NextExpiryTimestamp = long.MaxValue;
        }

        if (holder.IsSchemaChange)
        {
            for (var schemaIndex = 0; schemaIndex < _schemaHolders.Count; schemaIndex++)
            {
                if (ReferenceEquals(_schemaHolders[schemaIndex], holder))
                {
                    var lastSchema = _schemaHolders.Count - 1;
                    _schemaHolders[schemaIndex] = _schemaHolders[lastSchema];
                    _schemaHolders.RemoveAt(lastSchema);
                    break;
                }
            }
        }

        UnindexSet(_writeKeys, holder.WriteKeys, holder);
        UnindexSet(_readKeys, holder.ReadKeys, holder);
        UnindexSet(_writeTables, holder.WriteTables, holder);
        UnindexSet(_scannedTables, holder.ScannedTables, holder);
    }

    public void FindConflicts(StrongLockHolder candidate, ref ConflictCollector collector)
    {
        if (_holders.Count == 0)
        {
            return;
        }

        if (candidate.IsSchemaChange)
        {
            for (var index = 0; index < _holders.Count; index++)
            {
                collector.Add(_holders[index]);
            }

            return;
        }

        for (var index = 0; index < _schemaHolders.Count; index++)
        {
            collector.Add(_schemaHolders[index]);
        }

        if (candidate.WriteKeys.Count > 0)
        {
            if (_writeKeys.Count > 0)
            {
                LookupSet(_writeKeys, candidate.WriteKeys, ref collector);
            }

            if (_readKeys.Count > 0)
            {
                LookupSet(_readKeys, candidate.WriteKeys, ref collector);
            }
        }

        if (candidate.ReadKeys.Count > 0 && _writeKeys.Count > 0)
        {
            LookupSet(_writeKeys, candidate.ReadKeys, ref collector);
        }

        if (candidate.WriteTables.Count > 0 && _scannedTables.Count > 0)
        {
            LookupSet(_scannedTables, candidate.WriteTables, ref collector);
        }

        if (candidate.ScannedTables.Count > 0 && _writeTables.Count > 0)
        {
            LookupSet(_writeTables, candidate.ScannedTables, ref collector);
        }
    }

    private static void IndexSet<T>(Dictionary<T, HolderSlot> map, IReadOnlySet<T> keys, StrongLockHolder holder)
        where T : notnull
    {
        if (keys.Count == 0)
        {
            return;
        }

        if (keys is HashSet<T> hashSet)
        {
            foreach (var key in hashSet)
            {
                ref var slot = ref CollectionsMarshal.GetValueRefOrAddDefault(map, key, out _);
                slot.Add(holder);
            }

            return;
        }

        if (keys is FrozenSet<T> frozen)
        {
            var items = frozen.Items;
            for (var index = 0; index < items.Length; index++)
            {
                ref var slot = ref CollectionsMarshal.GetValueRefOrAddDefault(map, items[index], out _);
                slot.Add(holder);
            }

            return;
        }

        foreach (var key in keys)
        {
            ref var slot = ref CollectionsMarshal.GetValueRefOrAddDefault(map, key, out _);
            slot.Add(holder);
        }
    }

    private static void UnindexSet<T>(Dictionary<T, HolderSlot> map, IReadOnlySet<T> keys, StrongLockHolder holder)
        where T : notnull
    {
        if (keys.Count == 0 || map.Count == 0)
        {
            return;
        }

        if (keys is HashSet<T> hashSet)
        {
            foreach (var key in hashSet)
            {
                RemoveFromSlot(map, key, holder);
            }

            return;
        }

        if (keys is FrozenSet<T> frozen)
        {
            var items = frozen.Items;
            for (var index = 0; index < items.Length; index++)
            {
                RemoveFromSlot(map, items[index], holder);
            }

            return;
        }

        foreach (var key in keys)
        {
            RemoveFromSlot(map, key, holder);
        }
    }

    private static void RemoveFromSlot<T>(Dictionary<T, HolderSlot> map, T key, StrongLockHolder holder)
        where T : notnull
    {
        ref var slot = ref CollectionsMarshal.GetValueRefOrNullRef(map, key);
        if (!Unsafe.IsNullRef(ref slot) && slot.Remove(holder))
        {
            _ = map.Remove(key);
        }
    }

    private static void LookupSet<T>(Dictionary<T, HolderSlot> map, IReadOnlySet<T> keys, ref ConflictCollector collector)
        where T : notnull
    {
        if (keys is HashSet<T> hashSet)
        {
            foreach (var key in hashSet)
            {
                ref var slot = ref CollectionsMarshal.GetValueRefOrNullRef(map, key);
                if (!Unsafe.IsNullRef(ref slot))
                {
                    collector.AddSlot(in slot);
                }
            }

            return;
        }

        if (keys is FrozenSet<T> frozen)
        {
            var items = frozen.Items;
            for (var index = 0; index < items.Length; index++)
            {
                ref var slot = ref CollectionsMarshal.GetValueRefOrNullRef(map, items[index]);
                if (!Unsafe.IsNullRef(ref slot))
                {
                    collector.AddSlot(in slot);
                }
            }

            return;
        }

        foreach (var key in keys)
        {
            ref var slot = ref CollectionsMarshal.GetValueRefOrNullRef(map, key);
            if (!Unsafe.IsNullRef(ref slot))
            {
                collector.AddSlot(in slot);
            }
        }
    }
}

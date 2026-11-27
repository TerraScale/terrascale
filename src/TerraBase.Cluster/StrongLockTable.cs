using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Runtime.InteropServices;

namespace TerraBase.Cluster;

public sealed record StrongLockHolder(
    Ulid DatabaseId,
    Ulid TxId,
    HlcTimestamp Priority,
    string CoordinatorNodeId,
    IReadOnlySet<RowKey> WriteKeys,
    IReadOnlySet<RowKey> ReadKeys,
    IReadOnlySet<string> WriteTables,
    IReadOnlySet<string> ScannedTables,
    bool IsSchemaChange,
    long ExpiresAtTimestamp)
{
    public static StrongLockHolder From(StrongPreparePayload payload, string coordinatorNodeId, long expiresAtTimestamp)
    {
        ArgumentNullException.ThrowIfNull(payload);
        IReadOnlySet<RowKey> writeKeys;
        IReadOnlySet<string> writeTables;
        if (payload.WriteKeys.Count == 0)
        {
            writeKeys = FrozenSet<RowKey>.Empty;
            writeTables = FrozenSet<string>.Empty;
        }
        else if (payload.WriteKeys.Count == 1)
        {
            var only = payload.WriteKeys[0];
            writeKeys = new SingleItemSet<RowKey>(only);
            writeTables = new SingleItemSet<string>(only.Table, StringComparer.OrdinalIgnoreCase);
        }
        else
        {
            var first = payload.WriteKeys[0];
            HashSet<RowKey>? multiKeys = null;
            HashSet<string>? multiTables = null;
            for (var index = 1; index < payload.WriteKeys.Count; index++)
            {
                var key = payload.WriteKeys[index];
                if (multiKeys is not null)
                {
                    _ = multiKeys.Add(key);
                }
                else if (!EqualityComparer<RowKey>.Default.Equals(first, key))
                {
                    multiKeys = new HashSet<RowKey>(payload.WriteKeys.Count) { first, key };
                }

                if (multiTables is not null)
                {
                    _ = multiTables.Add(key.Table);
                }
                else if (!string.Equals(first.Table, key.Table, StringComparison.OrdinalIgnoreCase))
                {
                    multiTables = new HashSet<string>(payload.WriteKeys.Count, StringComparer.OrdinalIgnoreCase) { first.Table, key.Table };
                }
            }

            writeKeys = multiKeys is not null ? multiKeys : new SingleItemSet<RowKey>(first);
            writeTables = multiTables is not null ? multiTables : new SingleItemSet<string>(first.Table, StringComparer.OrdinalIgnoreCase);
        }

        IReadOnlySet<RowKey> readKeys;
        if (payload.ReadSet.Count == 0)
        {
            readKeys = FrozenSet<RowKey>.Empty;
        }
        else if (payload.ReadSet.Count == 1)
        {
            readKeys = new SingleItemSet<RowKey>(payload.ReadSet[0].Key);
        }
        else
        {
            var first = payload.ReadSet[0].Key;
            HashSet<RowKey>? multi = null;
            for (var index = 1; index < payload.ReadSet.Count; index++)
            {
                var key = payload.ReadSet[index].Key;
                if (multi is not null)
                {
                    _ = multi.Add(key);
                }
                else if (!EqualityComparer<RowKey>.Default.Equals(first, key))
                {
                    multi = new HashSet<RowKey>(payload.ReadSet.Count) { first, key };
                }
            }

            readKeys = multi is not null ? multi : new SingleItemSet<RowKey>(first);
        }

        IReadOnlySet<string> scannedTables;
        if (payload.ScannedTables.Count == 0)
        {
            scannedTables = FrozenSet<string>.Empty;
        }
        else if (payload.ScannedTables.Count == 1)
        {
            scannedTables = new SingleItemSet<string>(payload.ScannedTables[0], StringComparer.OrdinalIgnoreCase);
        }
        else
        {
            var first = payload.ScannedTables[0];
            HashSet<string>? multi = null;
            for (var index = 1; index < payload.ScannedTables.Count; index++)
            {
                var table = payload.ScannedTables[index];
                if (multi is not null)
                {
                    _ = multi.Add(table);
                }
                else if (!string.Equals(first, table, StringComparison.OrdinalIgnoreCase))
                {
                    multi = new HashSet<string>(payload.ScannedTables.Count, StringComparer.OrdinalIgnoreCase) { first, table };
                }
            }

            scannedTables = multi is not null ? multi : new SingleItemSet<string>(first, StringComparer.OrdinalIgnoreCase);
        }

        return new StrongLockHolder(
            payload.DatabaseId,
            payload.TxId,
            payload.Priority,
            coordinatorNodeId,
            writeKeys,
            readKeys,
            writeTables,
            scannedTables,
            payload.IsSchemaChange,
            expiresAtTimestamp);
    }

    public bool ConflictsWith(StrongLockHolder other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return IsSchemaChange
            || other.IsSchemaChange
            || SetsOverlap(WriteKeys, other.WriteKeys)
            || SetsOverlap(WriteKeys, other.ReadKeys)
            || SetsOverlap(ReadKeys, other.WriteKeys)
            || SetsOverlap(WriteTables, other.ScannedTables)
            || SetsOverlap(ScannedTables, other.WriteTables);
    }

    private static bool SetsOverlap<T>(IReadOnlySet<T> left, IReadOnlySet<T> right)
    {
        if (left.Count == 0 || right.Count == 0)
        {
            return false;
        }

        var (smaller, larger) = left.Count <= right.Count ? (left, right) : (right, left);
        if (smaller is SingleItemSet<T> single)
        {
            return larger.Contains(single.Item);
        }

        if (smaller is HashSet<T> hashSet)
        {
            foreach (var item in hashSet)
            {
                if (larger.Contains(item))
                {
                    return true;
                }
            }

            return false;
        }

        if (smaller is FrozenSet<T> frozen)
        {
            var items = frozen.Items;
            for (var index = 0; index < items.Length; index++)
            {
                if (larger.Contains(items[index]))
                {
                    return true;
                }
            }

            return false;
        }

        return smaller.Overlaps(larger);
    }
}

public sealed record LockGranted(StrongLockHolder Holder);

public sealed record LockDenied(StrongLockHolder Winner);

public sealed record LockWoundRequired(IReadOnlyList<StrongLockHolder> Victims);

public readonly union LockAttempt(LockGranted, LockDenied, LockWoundRequired);

public sealed class StrongLockTable(TimeProvider timeProvider)
{
    private const int FinalizedCapacity = 65536;
    private const int MaxPooledBuckets = 64;

    private readonly Lock _gate = new();
    private readonly Dictionary<Ulid, DatabaseLockBucket> _databases = [];
    private readonly Stack<DatabaseLockBucket> _bucketPool = new(MaxPooledBuckets);
    private readonly HashSet<Ulid> _activeTxIds = [];
    private readonly Dictionary<Ulid, TaskCompletionSource> _released = [];
    private readonly HashSet<Ulid> _finalized = [];
    private readonly Queue<Ulid> _finalizedOrder = new();
    private readonly HashSet<(Ulid DatabaseId, Ulid TxId, string Coordinator)> _remoteAborts = [];
    private readonly Queue<(Ulid DatabaseId, Ulid TxId, string Coordinator)> _remoteAbortOrder = new();
    private int _activeCount;
    private long _expired;
    private long _wounded;

    public long ExpiredLeases => Interlocked.Read(ref _expired);

    public long Wounds => Interlocked.Read(ref _wounded);

    public int ActiveHolders => Volatile.Read(ref _activeCount);

    public long LeaseDeadline(TimeSpan lease) => timeProvider.GetTimestamp() + (long)(lease.TotalSeconds * timeProvider.TimestampFrequency);

    public LockAttempt TryAcquire(StrongLockHolder candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        lock (_gate)
        {
            if (_finalized.Contains(candidate.TxId) || _remoteAborts.Contains((candidate.DatabaseId, candidate.TxId, candidate.CoordinatorNodeId)))
            {
                return new LockDenied(candidate);
            }

            ref var bucketRef = ref CollectionsMarshal.GetValueRefOrAddDefault(_databases, candidate.DatabaseId, out var exists);
            if (!exists || bucketRef is null)
            {
                bucketRef = _bucketPool.TryPop(out var pooled) ? pooled : new DatabaseLockBucket();
            }

            var bucket = bucketRef;
            EvictExpired(bucket);
            if (bucket.TryGet(candidate.TxId, out var existing))
            {
                return string.Equals(existing.CoordinatorNodeId, candidate.CoordinatorNodeId, StringComparison.Ordinal)
                    ? new LockGranted(existing)
                    : new LockDenied(existing);
            }

            var collector = new ConflictCollector();
            bucket.FindConflicts(candidate, ref collector);
            if (!collector.HasConflicts)
            {
                bucket.Add(candidate);
                _ = _activeTxIds.Add(candidate.TxId);
                Volatile.Write(ref _activeCount, _activeCount + 1);
                return new LockGranted(candidate);
            }

            if (collector.Oldest is { } oldest && oldest.Priority <= candidate.Priority)
            {
                return new LockDenied(oldest);
            }

            return new LockWoundRequired(collector.ToArray());
        }
    }

    public async ValueTask<bool> WaitReleasedAsync(Ulid txId, TimeSpan timeout, CancellationToken cancellationToken)
    {
        Task released;
        lock (_gate)
        {
            if (!_activeTxIds.Contains(txId))
            {
                return true;
            }

            ref var signal = ref CollectionsMarshal.GetValueRefOrAddDefault(_released, txId, out var exists);
            if (!exists || signal is null)
            {
                signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            released = signal.Task;
        }

        using var delay = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var winner = await Task.WhenAny(released, Task.Delay(timeout, timeProvider, delay.Token));
        await delay.CancelAsync();
        cancellationToken.ThrowIfCancellationRequested();
        return winner == released;
    }

    public bool Release(Ulid databaseId, Ulid txId)
    {
        lock (_gate)
        {
            return ReleaseLocked(databaseId, txId);
        }
    }

    internal void AbortFrom(Ulid databaseId, Ulid txId, string coordinator)
    {
        lock (_gate)
        {
            var key = (databaseId, txId, coordinator);
            if (_remoteAborts.Add(key))
            {
                _remoteAbortOrder.Enqueue(key);
                if (_remoteAbortOrder.Count > FinalizedCapacity)
                {
                    _ = _remoteAborts.Remove(_remoteAbortOrder.Dequeue());
                }
            }

            if (_databases.TryGetValue(databaseId, out var bucket) && bucket.TryGet(txId, out var holder)
                && string.Equals(holder.CoordinatorNodeId, coordinator, StringComparison.Ordinal))
            {
                _ = ReleaseLocked(databaseId, txId);
            }
        }
    }

    public void Wound(StrongLockHolder victim)
    {
        ArgumentNullException.ThrowIfNull(victim);
        if (Release(victim.DatabaseId, victim.TxId))
        {
            Interlocked.Increment(ref _wounded);
        }
    }

    private bool ReleaseLocked(Ulid databaseId, Ulid txId)
    {
        if (_released.Count > 0 && _released.Remove(txId, out var signal))
        {
            signal.TrySetResult();
        }

        if (_finalized.Add(txId))
        {
            _finalizedOrder.Enqueue(txId);
            while (_finalizedOrder.Count > FinalizedCapacity)
            {
                _ = _finalized.Remove(_finalizedOrder.Dequeue());
            }
        }

        if (!_databases.TryGetValue(databaseId, out var bucket) || !bucket.Remove(txId))
        {
            return false;
        }

        _ = _activeTxIds.Remove(txId);
        Volatile.Write(ref _activeCount, _activeCount - 1);
        if (bucket.Count == 0)
        {
            _ = _databases.Remove(databaseId);
            if (_bucketPool.Count < MaxPooledBuckets)
            {
                bucket.Clear();
                _bucketPool.Push(bucket);
            }
        }

        return true;
    }

    private void EvictExpired(DatabaseLockBucket bucket)
    {
        if (bucket.Count == 0)
        {
            return;
        }

        var now = timeProvider.GetTimestamp();
        if (now < bucket.NextExpiryTimestamp)
        {
            return;
        }

        var minExpiry = long.MaxValue;
        var evicted = 0;
        var holders = bucket.Holders;
        for (var index = holders.Count - 1; index >= 0; index--)
        {
            var holder = holders[index];
            if (holder.ExpiresAtTimestamp <= now)
            {
                bucket.RemoveAt(index);
                _ = _activeTxIds.Remove(holder.TxId);
                if (_released.Count > 0 && _released.Remove(holder.TxId, out var signal))
                {
                    signal.TrySetResult();
                }

                evicted++;
            }
            else if (holder.ExpiresAtTimestamp < minExpiry)
            {
                minExpiry = holder.ExpiresAtTimestamp;
            }
        }

        bucket.NextExpiryTimestamp = minExpiry;
        if (evicted > 0)
        {
            Volatile.Write(ref _activeCount, _activeCount - evicted);
            Interlocked.Add(ref _expired, evicted);
        }
    }
}

public enum WoundDecision : byte
{
    Aborted = 0,
    Committed = 1,
    Committing = 2,
}

internal enum StrongTransactionState
{
    Preparing = 0,
    Committing = 1,
    Aborted = 2,
}

internal sealed class StrongTransactionHandle(StrongPreparePayload payload)
{
    private readonly Lock _gate = new();
    private int _state;
    private string? _first;
    private List<string>? _more;

    public Ulid TxId => payload.TxId;

    public void DispatchTo(string nodeId)
    {
        lock (_gate)
        {
            if (_first is null)
            {
                _first = nodeId;
                return;
            }

            if (string.Equals(_first, nodeId, StringComparison.Ordinal))
            {
                return;
            }

            if (_more is null)
            {
                _more = [nodeId];
                return;
            }

            for (var index = 0; index < _more.Count; index++)
            {
                if (string.Equals(_more[index], nodeId, StringComparison.Ordinal))
                {
                    return;
                }
            }

            _more.Add(nodeId);
        }
    }

    public bool CanWound(StrongWoundPayload wound, string nodeId)
    {
        if (wound.DatabaseId != payload.DatabaseId || wound.WinnerPriority >= payload.Priority)
        {
            return false;
        }

        lock (_gate)
        {
            if (string.Equals(_first, nodeId, StringComparison.Ordinal))
            {
                return true;
            }

            if (_more is null)
            {
                return false;
            }

            for (var index = 0; index < _more.Count; index++)
            {
                if (string.Equals(_more[index], nodeId, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }
    }

    public StrongTransactionState State => (StrongTransactionState)Volatile.Read(ref _state);

    public bool TryBeginCommit() =>
        Interlocked.CompareExchange(ref _state, (int)StrongTransactionState.Committing, (int)StrongTransactionState.Preparing) == (int)StrongTransactionState.Preparing;

    public WoundDecision TryAbort()
    {
        var previous = Interlocked.CompareExchange(ref _state, (int)StrongTransactionState.Aborted, (int)StrongTransactionState.Preparing);
        return previous == (int)StrongTransactionState.Committing ? WoundDecision.Committing : WoundDecision.Aborted;
    }

    public void MarkAborted() => Volatile.Write(ref _state, (int)StrongTransactionState.Aborted);
}

public sealed class StrongTransactionRegistry
{
    private const int DecisionCapacity = 65536;

    private readonly ConcurrentDictionary<Ulid, StrongTransactionHandle> _active = new();
    private readonly Lock _gate = new();
    private readonly HashSet<Ulid> _committed = [];
    private readonly Queue<Ulid> _committedOrder = new();

    public int Active => _active.Count;

    internal StrongTransactionHandle Register(StrongPreparePayload payload)
    {
        var handle = new StrongTransactionHandle(payload);
        return _active.GetOrAdd(payload.TxId, handle);
    }

    internal WoundDecision? TryWoundFrom(StrongWoundPayload wound, string nodeId)
    {
        if (_active.TryGetValue(wound.TxId, out var handle))
        {
            return handle.CanWound(wound, nodeId) ? handle.TryAbort() : null;
        }

        lock (_gate)
        {
            return _committed.Contains(wound.TxId) ? WoundDecision.Committed : WoundDecision.Aborted;
        }
    }

    public WoundDecision TryWound(Ulid txId)
    {
        if (_active.TryGetValue(txId, out var handle))
        {
            return handle.TryAbort();
        }

        lock (_gate)
        {
            return _committed.Contains(txId) ? WoundDecision.Committed : WoundDecision.Aborted;
        }
    }

    internal void Complete(StrongTransactionHandle handle, bool committed)
    {
        if (committed)
        {
            lock (_gate)
            {
                if (_committed.Add(handle.TxId))
                {
                    _committedOrder.Enqueue(handle.TxId);
                    while (_committedOrder.Count > DecisionCapacity)
                    {
                        _ = _committed.Remove(_committedOrder.Dequeue());
                    }
                }
            }
        }
        else
        {
            handle.MarkAborted();
        }

        _ = _active.TryRemove(handle.TxId, out _);
    }
}

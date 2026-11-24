using System.Collections.Concurrent;

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
        return new StrongLockHolder(
            payload.DatabaseId,
            payload.TxId,
            payload.Priority,
            coordinatorNodeId,
            payload.WriteKeys.ToHashSet(),
            payload.ReadSet.Select(static read => read.Key).ToHashSet(),
            payload.WriteKeys.Select(static key => key.Table).ToHashSet(StringComparer.OrdinalIgnoreCase),
            payload.ScannedTables.ToHashSet(StringComparer.OrdinalIgnoreCase),
            payload.IsSchemaChange,
            expiresAtTimestamp);
    }

    public bool ConflictsWith(StrongLockHolder other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return IsSchemaChange
            || other.IsSchemaChange
            || WriteKeys.Overlaps(other.WriteKeys)
            || WriteKeys.Overlaps(other.ReadKeys)
            || ReadKeys.Overlaps(other.WriteKeys)
            || WriteTables.Overlaps(other.ScannedTables)
            || ScannedTables.Overlaps(other.WriteTables);
    }
}

public sealed record LockGranted(StrongLockHolder Holder);

public sealed record LockDenied(StrongLockHolder Winner);

public sealed record LockWoundRequired(IReadOnlyList<StrongLockHolder> Victims);

public readonly union LockAttempt(LockGranted, LockDenied, LockWoundRequired);

public sealed class StrongLockTable(TimeProvider timeProvider)
{
    private const int FinalizedCapacity = 65536;

    private readonly Lock _gate = new();
    private readonly Dictionary<Ulid, Dictionary<Ulid, StrongLockHolder>> _databases = [];
    private readonly Dictionary<Ulid, TaskCompletionSource> _released = [];
    private readonly HashSet<Ulid> _finalized = [];
    private readonly Queue<Ulid> _finalizedOrder = new();
    private readonly HashSet<(Ulid DatabaseId, Ulid TxId, string Coordinator)> _remoteAborts = [];
    private readonly Queue<(Ulid DatabaseId, Ulid TxId, string Coordinator)> _remoteAbortOrder = new();
    private long _expired;
    private long _wounded;

    public long ExpiredLeases => Interlocked.Read(ref _expired);

    public long Wounds => Interlocked.Read(ref _wounded);

    public int ActiveHolders
    {
        get
        {
            lock (_gate)
            {
                return _databases.Values.Sum(static holders => holders.Count);
            }
        }
    }

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

            if (!_databases.TryGetValue(candidate.DatabaseId, out var holders))
            {
                holders = [];
                _databases[candidate.DatabaseId] = holders;
            }

            EvictExpired(holders);
            if (holders.TryGetValue(candidate.TxId, out var existing))
            {
                return string.Equals(existing.CoordinatorNodeId, candidate.CoordinatorNodeId, StringComparison.Ordinal)
                    ? new LockGranted(existing)
                    : new LockDenied(existing);
            }

            List<StrongLockHolder> conflicts = [.. holders.Values.Where(candidate.ConflictsWith)];
            if (conflicts.Count == 0)
            {
                holders[candidate.TxId] = candidate;
                _released[candidate.TxId] = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                return new LockGranted(candidate);
            }

            var oldest = conflicts.MinBy(static holder => holder.Priority);
            if (oldest is not null && oldest.Priority <= candidate.Priority)
            {
                return new LockDenied(oldest);
            }

            return new LockWoundRequired(conflicts);
        }
    }

    public async ValueTask<bool> WaitReleasedAsync(Ulid txId, TimeSpan timeout, CancellationToken cancellationToken)
    {
        Task released;
        lock (_gate)
        {
            if (!_released.TryGetValue(txId, out var signal))
            {
                return true;
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
            if (_released.Remove(txId, out var signal))
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

            if (!_databases.TryGetValue(databaseId, out var holders) || !holders.Remove(txId))
            {
                return false;
            }

            if (holders.Count == 0)
            {
                _ = _databases.Remove(databaseId);
            }

            return true;
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

            if (_databases.TryGetValue(databaseId, out var holders) && holders.TryGetValue(txId, out var holder)
                && string.Equals(holder.CoordinatorNodeId, coordinator, StringComparison.Ordinal))
            {
                _ = Release(databaseId, txId);
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

    private void EvictExpired(Dictionary<Ulid, StrongLockHolder> holders)
    {
        var now = timeProvider.GetTimestamp();
        foreach (var (txId, holder) in holders.ToArray())
        {
            if (holder.ExpiresAtTimestamp <= now && holders.Remove(txId))
            {
                if (_released.Remove(txId, out var signal))
                {
                    signal.TrySetResult();
                }

                Interlocked.Increment(ref _expired);
            }
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
    private int _state;

    private readonly ConcurrentDictionary<string, byte> _participants = new(StringComparer.Ordinal);

    public Ulid TxId => payload.TxId;

    public void DispatchTo(string nodeId) => _ = _participants.TryAdd(nodeId, 0);

    public bool CanWound(StrongWoundPayload wound, string nodeId) =>
        wound.DatabaseId == payload.DatabaseId && wound.WinnerPriority < payload.Priority && _participants.ContainsKey(nodeId);

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

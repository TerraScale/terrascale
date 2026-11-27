using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Tasks.Sources;
using Microsoft.Extensions.Logging;

namespace TerraBase.Continuity;

public sealed class ChangefeedHub(ContinuitySessions sessions, ContinuityOptions options, ContinuityMetrics metrics, ILogger<ChangefeedHub> logger) : IChangefeedHub
{
    private const int ReplayPageSize = 256;

    private readonly Lock _gate = new();
    private readonly ConcurrentDictionary<Ulid, ChangefeedSubscriber[]> _subscribers = new();

    public ArchiveActivity Activity { get; } = new();

    public void ObserveBookmark(Ulid databaseId, CausalBookmark bookmark) => Activity.ObserveBookmark(databaseId, bookmark);

    public ValueTask PublishAsync(ChangefeedEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        Activity.Observe(entry.DatabaseId, entry.OriginRegion, entry.Sequence);
        if (!_subscribers.TryGetValue(entry.DatabaseId, out var targets))
        {
            return ValueTask.CompletedTask;
        }

        for (var index = 0; index < targets.Length; index++)
        {
            var target = targets[index];
            if (!target.TryEnqueue(entry))
            {
                Detach(entry.DatabaseId, target);
                target.Complete();
            }
        }

        return ValueTask.CompletedTask;
    }

    public IAsyncEnumerable<ChangefeedEntry> SubscribeAsync(ChangefeedSubscription subscription, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(subscription);
        return new ChangefeedEnumerable(this, metrics, options.ChangefeedSubscriberCapacity, subscription, cancellationToken);
    }

    private async IAsyncEnumerable<ChangefeedEntry> ReplayAsync(ChangefeedSubscription subscription, ChangefeedCursor cursor, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var opened = await sessions.OpenAsync(subscription.DatabaseId, cancellationToken);
        var sessionOrStop = opened switch
        {
            ISqlDatabaseSession available => available,
            ContinuitySessionMissing or ContinuitySessionUnavailable => null,
        };
        if (sessionOrStop is not { } session)
        {
            yield break;
        }

        await using (session)
        {
            var upTo = session.CurrentBookmark;
            foreach (var (origin, last) in upTo.Entries)
            {
                var next = subscription.After.Get(origin) + 1;
                while (next <= last && !cancellationToken.IsCancellationRequested)
                {
                    var read = await session.ReadCommitLogAsync(origin, next, ReplayPageSize, cancellationToken);
                    var page = read switch
                    {
                        CommitLogPage log => log,
                        CommitLogGap gap => Skip(subscription.DatabaseId, origin, gap),
                        StorageCorrupted corrupted => Stop(subscription.DatabaseId, origin, corrupted.Message),
                        StorageBusy busy => Stop(subscription.DatabaseId, origin, busy.Message),
                    };
                    if (page.Entries.Count == 0)
                    {
                        next = page.NextSequence > next ? page.NextSequence : last + 1;
                        continue;
                    }

                    foreach (var commit in page.Entries)
                    {
                        if (commit.Sequence > last)
                        {
                            break;
                        }

                        yield return new ChangefeedEntry(
                            subscription.DatabaseId,
                            commit.TxId,
                            commit.OriginRegion,
                            commit.Sequence,
                            commit.SchemaEpoch,
                            commit.Mode,
                            commit.DeltaPayload,
                            commit.Hlc.WallTime);
                    }

                    next = page.NextSequence;
                }

                cursor.Replayed(origin, last);
            }
        }
    }

    private CommitLogPage Skip(Ulid databaseId, string origin, CommitLogGap gap)
    {
        ContinuityLog.ReplayStopped(logger, databaseId, origin, "history before the oldest retained entry is no longer available; resuming from it");
        return new CommitLogPage(databaseId, origin, [], gap.OldestAvailable);
    }

    private CommitLogPage Stop(Ulid databaseId, string origin, string reason)
    {
        ContinuityLog.ReplayStopped(logger, databaseId, origin, reason);
        return new CommitLogPage(databaseId, origin, [], long.MaxValue);
    }

    private void Attach(Ulid databaseId, ChangefeedSubscriber subscriber)
    {
        lock (_gate)
        {
            if (_subscribers.TryGetValue(databaseId, out var existing))
            {
                var next = new ChangefeedSubscriber[existing.Length + 1];
                existing.CopyTo(next, 0);
                next[existing.Length] = subscriber;
                _subscribers[databaseId] = next;
            }
            else
            {
                _subscribers[databaseId] = [subscriber];
            }
        }

        metrics.SubscriberOpened();
    }

    private void Detach(Ulid databaseId, ChangefeedSubscriber subscriber)
    {
        var removed = false;
        lock (_gate)
        {
            if (_subscribers.TryGetValue(databaseId, out var existing))
            {
                var index = Array.IndexOf(existing, subscriber);
                if (index >= 0)
                {
                    removed = true;
                    if (existing.Length == 1)
                    {
                        _ = _subscribers.TryRemove(databaseId, out _);
                    }
                    else
                    {
                        var remaining = new ChangefeedSubscriber[existing.Length - 1];
                        if (index > 0)
                        {
                            Array.Copy(existing, 0, remaining, 0, index);
                        }

                        if (index < existing.Length - 1)
                        {
                            Array.Copy(existing, index + 1, remaining, index, existing.Length - index - 1);
                        }

                        _subscribers[databaseId] = remaining;
                    }
                }
            }
        }

        if (removed)
        {
            metrics.SubscriberClosed();
        }
    }

    private sealed class ChangefeedEnumerable(
        ChangefeedHub hub,
        ContinuityMetrics metrics,
        int capacity,
        ChangefeedSubscription subscription,
        CancellationToken subscribeToken) : IAsyncEnumerable<ChangefeedEntry>
    {
        public IAsyncEnumerator<ChangefeedEntry> GetAsyncEnumerator(CancellationToken cancellationToken = default) =>
            new ChangefeedSubscriber(hub, metrics, capacity, subscription, subscribeToken, cancellationToken);
    }

    private sealed class ChangefeedSubscriber : IAsyncEnumerator<ChangefeedEntry>, IValueTaskSource<bool>
    {
        private readonly ChangefeedHub _hub;
        private readonly ChangefeedSubscription _subscription;
        private readonly ContinuityMetrics _metrics;
        private readonly Lock _gate = new();
        private readonly ChangefeedEntry?[] _buffer;
        private readonly ChangefeedCursor _cursor;
        private readonly CancellationTokenSource? _linkedCts;
        private readonly CancellationToken _cancellationToken;
        private CancellationTokenRegistration _registration;
        private ManualResetValueTaskSourceCore<bool> _signal;
        private IAsyncEnumerator<ChangefeedEntry>? _replayEnumerator;
        private ChangefeedEntry? _current;
        private int _head;
        private int _tail;
        private int _count;
        private bool _attached;
        private bool _live;
        private bool _waiting;
        private bool _completed;
        private bool _disposed;

        public ChangefeedSubscriber(
            ChangefeedHub hub,
            ContinuityMetrics metrics,
            int capacity,
            ChangefeedSubscription subscription,
            CancellationToken subscribeToken,
            CancellationToken enumeratorToken)
        {
            _hub = hub;
            _subscription = subscription;
            _metrics = metrics;
            _buffer = new ChangefeedEntry?[capacity];
            _cursor = new ChangefeedCursor(subscription.After);
            _signal.RunContinuationsAsynchronously = true;
            if (!enumeratorToken.CanBeCanceled || enumeratorToken == subscribeToken)
            {
                _cancellationToken = subscribeToken;
            }
            else if (!subscribeToken.CanBeCanceled)
            {
                _cancellationToken = enumeratorToken;
            }
            else
            {
                _linkedCts = CancellationTokenSource.CreateLinkedTokenSource(subscribeToken, enumeratorToken);
                _cancellationToken = _linkedCts.Token;
            }
        }

        public ChangefeedEntry Current => _current ?? throw new InvalidOperationException("MoveNextAsync must return true before reading Current.");

        public ValueTask<bool> MoveNextAsync() => _live ? MoveNextLive() : MoveNextReplayAsync();

        public bool TryEnqueue(ChangefeedEntry entry)
        {
            var signal = false;
            lock (_gate)
            {
                if (_completed)
                {
                    return false;
                }

                if (_waiting)
                {
                    if (!_cursor.Accept(entry))
                    {
                        return true;
                    }

                    _waiting = false;
                    _current = entry;
                    signal = true;
                }
                else
                {
                    if (_count == _buffer.Length)
                    {
                        _completed = true;
                        return false;
                    }

                    _buffer[_tail] = entry;
                    var nextTail = _tail + 1;
                    _tail = nextTail == _buffer.Length ? 0 : nextTail;
                    _count++;
                }
            }

            if (signal)
            {
                _metrics.Delivered();
                _signal.SetResult(true);
            }

            return true;
        }

        public void Complete()
        {
            var signal = false;
            lock (_gate)
            {
                _completed = true;
                if (_waiting)
                {
                    _waiting = false;
                    signal = true;
                }
            }

            if (signal)
            {
                _signal.SetResult(false);
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            await _registration.DisposeAsync();
            if (_attached)
            {
                _hub.Detach(_subscription.DatabaseId, this);
            }

            Complete();
            await FinishReplayAsync();
            lock (_gate)
            {
                Array.Clear(_buffer);
                _head = 0;
                _tail = 0;
                _count = 0;
                _current = null;
            }

            _linkedCts?.Dispose();
        }

        bool IValueTaskSource<bool>.GetResult(short token) => _signal.GetResult(token);

        ValueTaskSourceStatus IValueTaskSource<bool>.GetStatus(short token) => _signal.GetStatus(token);

        void IValueTaskSource<bool>.OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags) =>
            _signal.OnCompleted(continuation, state, token, flags);

        private ValueTask<bool> MoveNextLive()
        {
            if (_cancellationToken.IsCancellationRequested)
            {
                return ValueTask.FromResult(false);
            }

            ChangefeedEntry? next = null;
            short version = 0;
            lock (_gate)
            {
                if (_cancellationToken.IsCancellationRequested)
                {
                    return ValueTask.FromResult(false);
                }

                while (_count > 0)
                {
                    var candidate = _buffer[_head];
                    _buffer[_head] = null;
                    var nextHead = _head + 1;
                    _head = nextHead == _buffer.Length ? 0 : nextHead;
                    _count--;
                    if (candidate is not null && _cursor.Accept(candidate))
                    {
                        next = candidate;
                        break;
                    }
                }

                if (next is null)
                {
                    if (_completed)
                    {
                        return ValueTask.FromResult(false);
                    }

                    _signal.Reset();
                    _waiting = true;
                    version = _signal.Version;
                }
            }

            if (next is not null)
            {
                _current = next;
                _metrics.Delivered();
                return ValueTask.FromResult(true);
            }

            return new ValueTask<bool>(this, version);
        }

        private async ValueTask<bool> MoveNextReplayAsync()
        {
            if (_cancellationToken.IsCancellationRequested)
            {
                _live = true;
                Complete();
                return false;
            }

            if (!_attached)
            {
                _attached = true;
                _hub.Attach(_subscription.DatabaseId, this);
                _registration = _cancellationToken.UnsafeRegister(
                    static state =>
                    {
                        if (state is ChangefeedSubscriber subscriber)
                        {
                            subscriber.Complete();
                        }
                    },
                    this);
                _replayEnumerator = _hub.ReplayAsync(_subscription, _cursor, _cancellationToken).GetAsyncEnumerator(_cancellationToken);
            }

            while (_replayEnumerator is { } replay)
            {
                if (_cancellationToken.IsCancellationRequested)
                {
                    await FinishReplayAsync();
                    return false;
                }

                if (!await replay.MoveNextAsync())
                {
                    await FinishReplayAsync();
                    break;
                }

                if (_cancellationToken.IsCancellationRequested)
                {
                    await FinishReplayAsync();
                    return false;
                }

                var entry = replay.Current;
                if (_cursor.Accept(entry))
                {
                    _current = entry;
                    _metrics.Delivered();
                    return true;
                }
            }

            _live = true;
            return await MoveNextLive();
        }

        private async ValueTask FinishReplayAsync()
        {
            if (_replayEnumerator is { } replay)
            {
                _replayEnumerator = null;
                await replay.DisposeAsync();
            }
        }
    }
}

internal sealed class ChangefeedCursor
{
    private const int RecentCapacity = 4096;

    private HashSet<Ulid>? _recent;
    private Queue<Ulid>? _order;
    private string? _singleOrigin;
    private long _singleFloor;
    private Dictionary<string, long>? _floor;
    private bool _hasOutOfOrder;

    public ChangefeedCursor(CausalBookmark after)
    {
        if (after.Entries.Count == 1)
        {
            var entry = after.Entries[0];
            _singleOrigin = entry.Key;
            _singleFloor = entry.Value;
        }
        else if (after.Entries.Count > 1)
        {
            _floor = new Dictionary<string, long>(after.Entries.Count, StringComparer.Ordinal);
            foreach (var (origin, sequence) in after.Entries)
            {
                _floor[origin] = sequence;
            }
        }
    }

    public void Replayed(string origin, long through)
    {
        if (_floor is null)
        {
            if (_singleOrigin is null)
            {
                _singleOrigin = origin;
                _singleFloor = through;
                return;
            }

            if (string.Equals(_singleOrigin, origin, StringComparison.Ordinal))
            {
                if (through > _singleFloor)
                {
                    _singleFloor = through;
                }

                return;
            }

            _floor = new Dictionary<string, long>(2, StringComparer.Ordinal)
            {
                [_singleOrigin] = _singleFloor,
            };
            _singleOrigin = null;
        }

        if (through > _floor.GetValueOrDefault(origin))
        {
            _floor[origin] = through;
        }
    }

    public bool Accept(ChangefeedEntry entry)
    {
        if (_floor is null)
        {
            if (_singleOrigin is null)
            {
                if (!_hasOutOfOrder && entry.Sequence == 1)
                {
                    _singleOrigin = entry.OriginRegion;
                    _singleFloor = 1;
                    return true;
                }
            }
            else if (string.Equals(_singleOrigin, entry.OriginRegion, StringComparison.Ordinal))
            {
                if (entry.Sequence <= _singleFloor)
                {
                    return false;
                }

                if (!_hasOutOfOrder && entry.Sequence == _singleFloor + 1)
                {
                    _singleFloor = entry.Sequence;
                    return true;
                }
            }
            else
            {
                _floor = new Dictionary<string, long>(2, StringComparer.Ordinal)
                {
                    [_singleOrigin] = _singleFloor,
                };
                _singleOrigin = null;
                _singleFloor = 0;
            }
        }

        if (_floor is not null)
        {
            var floor = _floor.GetValueOrDefault(entry.OriginRegion);
            if (entry.Sequence <= floor)
            {
                return false;
            }

            if (!_hasOutOfOrder && entry.Sequence == floor + 1)
            {
                _floor[entry.OriginRegion] = entry.Sequence;
                return true;
            }
        }

        _hasOutOfOrder = true;
        _recent ??= [];
        _order ??= new Queue<Ulid>();
        if (!_recent.Add(entry.TxId))
        {
            return false;
        }

        _order.Enqueue(entry.TxId);
        if (_order.Count > RecentCapacity)
        {
            _ = _recent.Remove(_order.Dequeue());
        }

        return true;
    }
}

using System.Collections.Concurrent;
using System.Net.Quic;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using System.Threading.Tasks.Sources;
using TerraScale.Contracts.Quic;

namespace TerraScale.QuicRpc;

internal sealed record ChannelEvicted;

internal readonly union PushDeliveryOutcome(QuicSent, QuicFault);

internal readonly union OrderedPushOutcome(QuicSent, ChannelEvicted, QuicFault);

internal sealed record TurnGranted;

internal readonly union TurnOutcome(TurnGranted, QuicFault);

internal sealed class PendingTurn : IValueTaskSource<TurnOutcome>
{
    private const int Queued = 0;
    private const int Claimed = 1;
    private const int Abandoned = 2;
    private const int MaxPoolSize = 64;

    private static readonly TurnGranted Granted = new();
    private static readonly ConcurrentQueue<PendingTurn> Pool = new();
    private static int PoolCount;

    [ThreadStatic]
    private static PendingTurn? ThreadLocal;

    private ManualResetValueTaskSourceCore<TurnOutcome> _core;
    private CallScope _scope;
    private int _state;
    private int _refs;

    private PendingTurn()
    {
        _core.RunContinuationsAsynchronously = true;
    }

    public short Version => _core.Version;

    public static PendingTurn Rent(CallScope scope)
    {
        var turn = ThreadLocal;
        if (turn is not null)
        {
            ThreadLocal = null;
        }
        else if (Pool.TryDequeue(out turn))
        {
            Interlocked.Decrement(ref PoolCount);
        }
        else
        {
            turn = new PendingTurn();
        }

        turn._scope = scope;
        Volatile.Write(ref turn._state, Queued);
        Volatile.Write(ref turn._refs, 2);
        return turn;
    }

    public ValueTask<TurnOutcome> WaitAsync(short version) => new(this, version);

    public bool TryGrant()
    {
        var won = Interlocked.CompareExchange(ref _state, Claimed, Queued) == Queued;
        if (won)
        {
            _core.SetResult(Granted);
        }

        ReleaseRef();
        return won;
    }

    public void Fail(QuicFault fault)
    {
        if (Interlocked.CompareExchange(ref _state, Claimed, Queued) == Queued)
        {
            _core.SetResult(fault);
        }

        ReleaseRef();
    }

    public void Abandon()
    {
        if (Interlocked.CompareExchange(ref _state, Abandoned, Queued) != Queued)
        {
            return;
        }

        if (_scope.Caller.IsCancellationRequested)
        {
            _core.SetException(new OperationCanceledException(_scope.Caller));
            return;
        }

        _core.SetResult(_scope.Interrupted());
    }

    public void ReleaseAwaiter() => ReleaseRef();

    public TurnOutcome GetResult(short token) => _core.GetResult(token);

    public ValueTaskSourceStatus GetStatus(short token) => _core.GetStatus(token);

    public void OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags) =>
        _core.OnCompleted(continuation, state, token, flags);

    private void ReleaseRef()
    {
        if (Interlocked.Decrement(ref _refs) != 0)
        {
            return;
        }

        _scope = default;
        _core.Reset();

        if (ThreadLocal is null)
        {
            ThreadLocal = this;
        }
        else if (Interlocked.Increment(ref PoolCount) <= MaxPoolSize)
        {
            Pool.Enqueue(this);
        }
        else
        {
            Interlocked.Decrement(ref PoolCount);
        }
    }
}

[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
[SupportedOSPlatform("windows")]
internal sealed class OrderedPushChannel : IAsyncDisposable
{
    private static readonly ChannelEvicted Evicted = new();
    private static readonly Action<object?> CancelTurnCallback = static state =>
    {
        if (state is PendingTurn pending)
        {
            pending.Abandon();
        }
    };

    private readonly QuicConnectionPool _pool;
    private readonly PeerSlot _slot;
    private readonly string _key;
    private readonly Lock _gate = new();
    private readonly Lock _stopGate = new();
    private readonly Queue<PendingTurn> _waiters = new();
    private readonly Queue<TaskCompletionSource<bool>> _capacityWaiters = new();
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _stop;
    private readonly ITimer _idleTimer;
    private ReusableCallScope _writeScope;
    private StreamLease? _lease;
    private QuicStream? _activeStream;
    private long _lastActivityTicks;
    private bool _writing;
    private bool _completing;
    private bool _evicted;
    private bool _closingStarted;
    private bool _idleTimerScheduled;
    private bool _stopDisposed;
    private int _disposed;

    public OrderedPushChannel(QuicConnectionPool pool, PeerSlot slot, string key)
    {
        _pool = pool;
        _slot = slot;
        _key = key;
        _lastActivityTicks = pool.TimeProvider.GetTimestamp();
        _stop = CancellationTokenSource.CreateLinkedTokenSource(pool.ShutdownToken);
        _writeScope = ReusableCallScope.Create(pool.TimeProvider, _stop.Token);
        _idleTimer = pool.TimeProvider.CreateTimer(
            static state =>
            {
                if (state is OrderedPushChannel channel)
                {
                    channel.OnIdleTimeout();
                }
            },
            this,
            QuicMeshLimits.OrderedChannelIdleTimeout,
            Timeout.InfiniteTimeSpan);
        _idleTimerScheduled = true;
    }

    public string Key => _key;

    public bool IsEvicted => Volatile.Read(ref _evicted);

    public Task Completion => _completion.Task;

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<OrderedPushOutcome>))]
    public async ValueTask<OrderedPushOutcome> PushAsync(ReadOnlyMemory<byte> frame, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_pool.ShutdownToken.IsCancellationRequested)
        {
            return new TransportShutdown("The QUIC transport is shutting down.");
        }

        var acquiredDirectly = false;
        lock (_gate)
        {
            if (_completing)
            {
                return _evicted
                    ? Evicted
                    : new TransportShutdown("The ordered push channel is closed.");
            }

            if (!_writing)
            {
                _writing = true;
                acquiredDirectly = true;
            }
        }

        if (!acquiredDirectly)
        {
            return await PushContendedAsync(frame, cancellationToken);
        }

        QuicFault? failure = null;
        var delivered = false;
        var writeCompleted = false;
        var scopeActive = false;
        var lease = _lease;
        try
        {
            if (_stop.IsCancellationRequested)
            {
                failure = new TransportShutdown("The ordered push channel is closed.");
                return failure;
            }

            var scope = _writeScope.Begin(QuicMeshLimits.CallTimeout, _pool.TimeProvider, cancellationToken);
            scopeActive = true;

            if (lease is not null && ReferenceEquals(_slot.Current(), lease.Connection))
            {
                var outcome = await QuicIo.WriteAsync(lease.Stream, frame, false, scope);
                writeCompleted = true;
                var (sent, writeFault) = outcome switch
                {
                    QuicSent done => (done, (QuicFault?)null),
                    QuicFault fault => ((QuicSent?)null, fault),
                };
                if (sent is not null)
                {
                    _pool.Metrics.Sent(QuicSide.Client, frame.Length);
                    delivered = true;
                    return sent;
                }

                if (writeFault is not null)
                {
                    if (_stop.IsCancellationRequested)
                    {
                        failure = Reset(lease, new TransportShutdown("The ordered push channel is closed."));
                        return failure;
                    }

                    if (QuicFaults.IsStreamGone(writeFault))
                    {
                        var reopened = await ReopenAsync(lease, writeFault, frame, scope);
                        var (reopenedSent, reopenedFault) = reopened switch
                        {
                            QuicSent done => (done, (QuicFault?)null),
                            QuicFault fault => ((QuicSent?)null, fault),
                        };
                        if (reopenedSent is not null)
                        {
                            delivered = true;
                            return reopenedSent;
                        }

                        failure = reopenedFault ?? new LocalAbort("The ordered push operation failed.");
                        return failure;
                    }

                    failure = Reset(lease, writeFault);
                    return failure;
                }
            }
            else if (lease is not null)
            {
                Retire(lease);
            }

            writeCompleted = true;
            var opened = await OpenAndWriteAsync(frame, scope);
            var (openedSent, openedFault) = opened switch
            {
                QuicSent done => (done, (QuicFault?)null),
                QuicFault fault => ((QuicSent?)null, fault),
            };
            if (openedSent is not null)
            {
                delivered = true;
                return openedSent;
            }

            failure = openedFault ?? new LocalAbort("The ordered push operation failed.");
            return failure;
        }
        finally
        {
            if (scopeActive)
            {
                _writeScope.End(_pool.TimeProvider);
            }

            if (!writeCompleted && lease is not null && ReferenceEquals(_lease, lease))
            {
                Retire(lease);
            }

            ReleaseTurn(delivered ? null : failure ?? new LocalAbort("The ordered push operation was canceled."));
        }
    }

    public void Complete()
    {
        var startClose = false;
        List<TaskCompletionSource<bool>>? capacityWaiters = null;
        lock (_gate)
        {
            _completing = true;
            if (_capacityWaiters.Count > 0)
            {
                capacityWaiters = [.. _capacityWaiters];
                _capacityWaiters.Clear();
            }

            if (!_writing && !_closingStarted)
            {
                _closingStarted = true;
                _writing = true;
                startClose = true;
            }
        }

        if (capacityWaiters is not null)
        {
            foreach (var waiter in capacityWaiters)
            {
                waiter.TrySetResult(false);
            }
        }

        if (startClose)
        {
            _ = CloseStreamAndFinishAsync();
        }
    }

    public bool TryCompleteIdle()
    {
        lock (_gate)
        {
            if (_completing || _writing || _waiters.Count > 0 || HasLiveCapacityWaitersLocked())
            {
                return false;
            }

            Volatile.Write(ref _evicted, true);
            _completing = true;
            _closingStarted = true;
            _writing = true;
        }

        _ = CloseStreamAndFinishAsync();
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Complete();
        CancelStop();
        Volatile.Read(ref _activeStream)?.Abort(QuicAbortDirection.Write, QuicProtocol.StreamErrorCode);
        await Completion;
        DisposeResources();
    }

    private async ValueTask<OrderedPushOutcome> PushContendedAsync(ReadOnlyMemory<byte> frame, CancellationToken cancellationToken)
    {
        using var scope = CallScope.Create(QuicMeshLimits.CallTimeout, _pool.TimeProvider, _pool.ShutdownToken, cancellationToken);
        PendingTurn? turn = null;
        short version = 0;
        while (true)
        {
            TaskCompletionSource<bool>? capacityWaiter = null;
            var acquiredDirectly = false;
            lock (_gate)
            {
                if (_completing)
                {
                    return _evicted
                        ? Evicted
                        : new TransportShutdown("The ordered push channel is closed.");
                }

                if (!_writing)
                {
                    _writing = true;
                    acquiredDirectly = true;
                }
                else if (_waiters.Count < QuicMeshLimits.OrderedPushQueueCapacity)
                {
                    turn = PendingTurn.Rent(scope);
                    version = turn.Version;
                    _waiters.Enqueue(turn);
                }
                else
                {
                    capacityWaiter = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    _capacityWaiters.Enqueue(capacityWaiter);
                }
            }

            if (acquiredDirectly)
            {
                return await DeliverAndReleaseAsync(frame, scope);
            }

            if (turn is not null)
            {
                break;
            }

            if (capacityWaiter is not null)
            {
                var wait = capacityWaiter.Task.WaitAsync(scope.Token);
                await ((Task)wait).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                if (!wait.IsCompletedSuccessfully)
                {
                    if (!capacityWaiter.TrySetCanceled(CancellationToken.None)
                        && capacityWaiter.Task.IsCompletedSuccessfully
                        && await capacityWaiter.Task)
                    {
                        ForwardCapacityWakeup();
                    }

                    if (wait.IsCanceled && !scope.Caller.IsCancellationRequested)
                    {
                        return scope.Interrupted();
                    }

                    await wait;
                }

                if (!await wait)
                {
                    return _evicted
                        ? Evicted
                        : new TransportShutdown("The ordered push channel is closed.");
                }
            }
        }

        TurnOutcome waited;
        try
        {
            using var registration = scope.Token.UnsafeRegister(CancelTurnCallback, turn);
            waited = await turn.WaitAsync(version);
        }
        finally
        {
            turn.ReleaseAwaiter();
        }

        return waited switch
        {
            TurnGranted => await DeliverAndReleaseAsync(frame, scope),
            QuicFault fault => fault,
        };
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<OrderedPushOutcome>))]
    private async ValueTask<OrderedPushOutcome> DeliverAndReleaseAsync(ReadOnlyMemory<byte> frame, CallScope scope)
    {
        QuicFault? failure = null;
        var delivered = false;
        try
        {
            var outcome = await DeliverAsync(frame, scope);
            var (sent, fault) = outcome switch
            {
                QuicSent done => (done, (QuicFault?)null),
                QuicFault error => ((QuicSent?)null, error),
            };
            if (sent is not null)
            {
                delivered = true;
                return sent;
            }

            failure = fault;
            return fault ?? new LocalAbort("The ordered push operation failed.");
        }
        finally
        {
            ReleaseTurn(delivered ? null : failure ?? new LocalAbort("The ordered push operation was canceled."));
        }
    }

    private void ReleaseTurn(QuicFault? failure)
    {
        List<PendingTurn>? failedWaiters = null;
        List<TaskCompletionSource<bool>>? capacityToWake = null;
        var startClose = false;
        var scheduleIdle = false;

        lock (_gate)
        {
            _lastActivityTicks = _pool.TimeProvider.GetTimestamp();
            if (failure is not null)
            {
                if (_waiters.Count > 0)
                {
                    failedWaiters = [.. _waiters];
                    _waiters.Clear();
                }

                while (TryDequeueLiveCapacityWaiterLocked() is { } capWaiter)
                {
                    capacityToWake ??= [];
                    capacityToWake.Add(capWaiter);
                }

                if (_completing && !_closingStarted)
                {
                    _closingStarted = true;
                    startClose = true;
                }
                else
                {
                    _writing = false;
                    if (!_idleTimerScheduled && !_completing)
                    {
                        _idleTimerScheduled = true;
                        scheduleIdle = true;
                    }
                }
            }
            else
            {
                var handedOff = false;
                while (_waiters.TryDequeue(out var next))
                {
                    if (TryDequeueLiveCapacityWaiterLocked() is { } capWaiter)
                    {
                        capacityToWake ??= [];
                        capacityToWake.Add(capWaiter);
                    }

                    if (next.TryGrant())
                    {
                        handedOff = true;
                        break;
                    }
                }

                if (!handedOff)
                {
                    while (TryDequeueLiveCapacityWaiterLocked() is { } capWaiter)
                    {
                        capacityToWake ??= [];
                        capacityToWake.Add(capWaiter);
                    }

                    if (_completing && !_closingStarted)
                    {
                        _closingStarted = true;
                        startClose = true;
                    }
                    else
                    {
                        _writing = false;
                        if (!_idleTimerScheduled && !_completing)
                        {
                            _idleTimerScheduled = true;
                            scheduleIdle = true;
                        }
                    }
                }
            }
        }

        if (failedWaiters is not null && failure is not null)
        {
            foreach (var waiter in failedWaiters)
            {
                waiter.Fail(failure);
            }
        }

        if (capacityToWake is not null)
        {
            var missed = 0;
            foreach (var waiter in capacityToWake)
            {
                if (!waiter.TrySetResult(true))
                {
                    missed++;
                }
            }

            for (var index = 0; index < missed; index++)
            {
                ForwardCapacityWakeup();
            }
        }

        if (scheduleIdle)
        {
            ChangeIdleTimer(QuicMeshLimits.OrderedChannelIdleTimeout);
        }

        if (startClose)
        {
            _ = CloseStreamAndFinishAsync();
        }
    }

    private bool HasLiveCapacityWaitersLocked()
    {
        while (_capacityWaiters.TryPeek(out var waiter) && waiter.Task.IsCompleted)
        {
            _ = _capacityWaiters.Dequeue();
        }

        return _capacityWaiters.Count > 0;
    }

    private TaskCompletionSource<bool>? TryDequeueLiveCapacityWaiterLocked()
    {
        while (_capacityWaiters.TryDequeue(out var waiter))
        {
            if (!waiter.Task.IsCompleted)
            {
                return waiter;
            }
        }

        return null;
    }

    private void ForwardCapacityWakeup()
    {
        while (true)
        {
            TaskCompletionSource<bool>? next = null;
            lock (_gate)
            {
                if (_completing)
                {
                    return;
                }

                next = TryDequeueLiveCapacityWaiterLocked();
            }

            if (next is null || next.TrySetResult(true))
            {
                return;
            }
        }
    }

    private void OnIdleTimeout()
    {
        TimeSpan? reschedule = null;
        var shouldEvict = false;

        lock (_gate)
        {
            _idleTimerScheduled = false;
            if (_completing || _writing || _waiters.Count > 0 || HasLiveCapacityWaitersLocked())
            {
                return;
            }

            var elapsed = _pool.TimeProvider.GetElapsedTime(_lastActivityTicks);
            if (elapsed < QuicMeshLimits.OrderedChannelIdleTimeout)
            {
                reschedule = QuicMeshLimits.OrderedChannelIdleTimeout - elapsed;
                _idleTimerScheduled = true;
            }
            else
            {
                shouldEvict = true;
            }
        }

        if (reschedule is { } remaining)
        {
            ChangeIdleTimer(remaining);
        }
        else if (shouldEvict)
        {
            _slot.EvictIdleChannel(_key, this);
        }
    }

    private void ChangeIdleTimer(TimeSpan dueTime)
    {
        lock (_stopGate)
        {
            if (!_stopDisposed)
            {
                _ = _idleTimer.Change(dueTime, Timeout.InfiniteTimeSpan);
            }
        }
    }

    private void CancelStop()
    {
        lock (_stopGate)
        {
            if (!_stopDisposed)
            {
                _stop.Cancel();
            }
        }
    }

    private void DisposeResources()
    {
        lock (_stopGate)
        {
            if (_stopDisposed)
            {
                return;
            }

            _stopDisposed = true;
            _idleTimer.Dispose();
            _writeScope.Dispose();
            _stop.Dispose();
        }
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<PushDeliveryOutcome>))]
    private async ValueTask<PushDeliveryOutcome> DeliverAsync(ReadOnlyMemory<byte> frame, CallScope scope)
    {
        if (_stop.IsCancellationRequested)
        {
            return new TransportShutdown("The ordered push channel is closed.");
        }

        if (_lease is { } lease)
        {
            if (ReferenceEquals(_slot.Current(), lease.Connection))
            {
                return await WriteAsync(lease, frame, true, scope);
            }

            Retire(lease);
        }

        return await OpenAndWriteAsync(frame, scope);
    }

    private async ValueTask<PushDeliveryOutcome> OpenAndWriteAsync(ReadOnlyMemory<byte> frame, CallScope scope)
    {
        using var openScope = CallScope.Create(QuicMeshLimits.CallTimeout, _pool.TimeProvider, _stop.Token, scope.Caller);
        var outcome = await _pool.OpenStreamAsync(_slot, QuicStreamType.Unidirectional, openScope);
        return outcome switch
        {
            StreamLease opened => await WriteAsync(Track(opened), frame, false, scope),
            QuicFault fault => fault,
        };
    }

    private StreamLease Track(StreamLease opened)
    {
        _lease = opened;
        Volatile.Write(ref _activeStream, opened.Stream);
        _pool.Metrics.StreamOpened(QuicSide.Client);
        return opened;
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<PushDeliveryOutcome>))]
    private async ValueTask<PushDeliveryOutcome> WriteAsync(StreamLease lease, ReadOnlyMemory<byte> frame, bool reused, CallScope scope)
    {
        var completed = false;
        try
        {
            var outcome = await QuicIo.WriteAsync(lease.Stream, frame, false, scope);
            completed = true;
            return outcome switch
            {
                QuicSent done => Sent(done, frame.Length),
                QuicFault fault when _stop.IsCancellationRequested => Reset(lease, new TransportShutdown("The ordered push channel is closed.")),
                QuicFault fault when reused && QuicFaults.IsStreamGone(fault) => await ReopenAsync(lease, fault, frame, scope),
                QuicFault fault => Reset(lease, fault),
            };
        }
        finally
        {
            if (!completed && ReferenceEquals(_lease, lease))
            {
                Retire(lease);
            }
        }
    }

    private async ValueTask<PushDeliveryOutcome> ReopenAsync(StreamLease lease, QuicFault fault, ReadOnlyMemory<byte> frame, CallScope scope)
    {
        _ = Reset(lease, fault);
        return await OpenAndWriteAsync(frame, scope);
    }

    private QuicSent Sent(QuicSent done, int length)
    {
        _pool.Metrics.Sent(QuicSide.Client, length);
        return done;
    }

    private QuicFault Reset(StreamLease lease, QuicFault fault)
    {
        _pool.Metrics.Error(QuicSide.Client, fault);
        Retire(lease);
        _pool.InvalidateIfDead(lease, fault);
        return fault;
    }

    private void Retire(StreamLease lease)
    {
        _lease = null;
        Volatile.Write(ref _activeStream, null);
        lease.Stream.Abort(QuicAbortDirection.Write, QuicProtocol.StreamErrorCode);
        lease.ReleaseFireAndForget();
        _pool.Metrics.StreamClosed(QuicSide.Client);
    }

    private async Task CloseStreamAndFinishAsync()
    {
        try
        {
            await CloseStreamAsync();
        }
        finally
        {
            if (_evicted)
            {
                DisposeResources();
            }

            _completion.TrySetResult();
        }
    }

    private async ValueTask CloseStreamAsync()
    {
        if (_lease is not { } lease)
        {
            return;
        }

        _lease = null;
        Volatile.Write(ref _activeStream, null);
        using var scope = CallScope.Create(QuicMeshLimits.DrainTimeout, _pool.TimeProvider, _stop.Token, CancellationToken.None);
        var completed = await QuicIo.WriteAsync(lease.Stream, ReadOnlyMemory<byte>.Empty, true, scope);
        if (completed switch { QuicSent => null, QuicFault fault => fault } is { } failure)
        {
            lease.Stream.Abort(QuicAbortDirection.Write, QuicProtocol.StreamErrorCode);
            _pool.Metrics.Error(QuicSide.Client, failure);
            _pool.InvalidateIfDead(lease, failure);
            lease.ReleaseFireAndForget();
        }
        else
        {
            await lease.ReleaseAsync();
        }

        _pool.Metrics.StreamClosed(QuicSide.Client);
    }
}

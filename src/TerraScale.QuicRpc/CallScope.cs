using System.Collections.Concurrent;

namespace TerraScale.QuicRpc;

internal readonly struct CallScope : IDisposable
{
    private readonly CallScopeState? _state;
    private readonly CancellationToken _shutdown;
    private readonly CancellationToken _caller;

    private CallScope(CallScopeState? state, CancellationToken shutdown, CancellationToken caller)
    {
        _state = state;
        _shutdown = shutdown;
        _caller = caller;
    }

    public static CallScope Create(TimeSpan timeout, TimeProvider timeProvider, CancellationToken shutdown, CancellationToken caller)
    {
        if (timeout == Timeout.InfiniteTimeSpan && (!caller.CanBeCanceled || !shutdown.CanBeCanceled))
        {
            return new CallScope(null, shutdown, caller);
        }

        return new CallScope(CallScopeState.Rent(timeout, timeProvider, shutdown, caller), shutdown, caller);
    }

    public static CallScope CreateArmable(TimeProvider timeProvider, CancellationToken shutdown, CancellationToken caller) =>
        new(CallScopeState.Rent(Timeout.InfiniteTimeSpan, timeProvider, shutdown, caller), shutdown, caller);

    public static CallScope FromToken(CancellationToken shutdown) => new(null, shutdown, CancellationToken.None);

    public CancellationToken Caller => _caller;

    public CancellationToken Token =>
        _state is not null ? _state.Token
        : _caller.CanBeCanceled ? _caller
        : _shutdown;

    public bool IsDeadlineExpired =>
        _state is not null && _state.IsDeadlineTriggered && !_shutdown.IsCancellationRequested && !_caller.IsCancellationRequested;

    public void DisarmDeadline() => _state?.DisarmDeadline();

    public void ArmDeadline(TimeSpan timeout, TimeProvider timeProvider) => _state?.ArmDeadline(timeout, timeProvider);

    public static void ReArm(ref CallScope scope, TimeSpan timeout, TimeProvider timeProvider)
    {
        if (!scope.IsDeadlineExpired)
        {
            scope.ArmDeadline(timeout, timeProvider);
            if (!scope.IsDeadlineExpired)
            {
                return;
            }
        }

        var shutdown = scope._shutdown;
        var caller = scope._caller;
        scope.Dispose();
        scope = Create(timeout, timeProvider, shutdown, caller);
    }

    public static void Disarm(ref CallScope scope, TimeProvider timeProvider)
    {
        scope.DisarmDeadline();
        if (scope.IsDeadlineExpired)
        {
            var shutdown = scope._shutdown;
            var caller = scope._caller;
            scope.Dispose();
            scope = CreateArmable(timeProvider, shutdown, caller);
        }
    }

    public QuicFault Interrupted() =>
        _shutdown.IsCancellationRequested ? new TransportShutdown("The QUIC transport is shutting down.")
        : _state is not null && _state.IsDeadlineTriggered ? new DeadlineExceeded("The QUIC operation exceeded its deadline.")
        : new LocalAbort("The QUIC operation was canceled.");

    public void Dispose() => _state?.Dispose();
}

internal sealed class CallScopeState : IDisposable
{
    private const int MaxPoolSize = 64;

    private static readonly ConcurrentQueue<CallScopeState> Pool = new();
    private static int PoolCount;

    [ThreadStatic]
    private static CallScopeState? ThreadLocal;

    private static readonly Action<object?> CancelCallback = static state =>
    {
        if (state is CancellationTokenSource cts)
        {
            cts.Cancel();
        }
    };

    private CancellationTokenSource _cts = new();
    private CancellationTokenSource? _customDeadline;
    private CancellationTokenRegistration _shutdownReg;
    private CancellationTokenRegistration _callerReg;
    private CancellationTokenRegistration _customDeadlineReg;
    private CancellationToken _shutdown;
    private CancellationToken _caller;
    private int _returned;

    private CallScopeState()
    {
    }

    public CancellationToken Token => _cts.Token;

    public bool IsDeadlineTriggered =>
        (_customDeadline is { IsCancellationRequested: true } || _cts.IsCancellationRequested)
        && !_shutdown.IsCancellationRequested
        && !_caller.IsCancellationRequested;

    public static CallScopeState Rent(TimeSpan timeout, TimeProvider timeProvider, CancellationToken shutdown, CancellationToken caller)
    {
        var state = ThreadLocal;
        if (state is not null)
        {
            ThreadLocal = null;
        }
        else if (Pool.TryDequeue(out state))
        {
            Interlocked.Decrement(ref PoolCount);
        }
        else
        {
            state = new CallScopeState();
        }

        state._returned = 0;
        state._shutdown = shutdown;
        state._caller = caller;
        var hasTimeout = timeout != Timeout.InfiniteTimeSpan;

        if (hasTimeout)
        {
            if (ReferenceEquals(timeProvider, TimeProvider.System))
            {
                state._cts.CancelAfter(timeout);
            }
            else
            {
                var custom = new CancellationTokenSource(timeout, timeProvider);
                state._customDeadline = custom;
                state._customDeadlineReg = custom.Token.UnsafeRegister(CancelCallback, state._cts);
            }
        }

        if (shutdown.CanBeCanceled)
        {
            state._shutdownReg = shutdown.UnsafeRegister(CancelCallback, state._cts);
        }

        if (caller.CanBeCanceled)
        {
            state._callerReg = caller.UnsafeRegister(CancelCallback, state._cts);
        }

        return state;
    }

    public void DisarmDeadline()
    {
        if (Volatile.Read(ref _returned) != 0 || _cts.IsCancellationRequested)
        {
            return;
        }

        if (_customDeadline is { } custom)
        {
            if (!custom.IsCancellationRequested)
            {
                custom.CancelAfter(Timeout.InfiniteTimeSpan);
            }
        }
        else
        {
            _cts.CancelAfter(Timeout.InfiniteTimeSpan);
        }
    }

    public void ArmDeadline(TimeSpan timeout, TimeProvider timeProvider)
    {
        if (Volatile.Read(ref _returned) != 0 || _cts.IsCancellationRequested)
        {
            return;
        }

        if (timeout == Timeout.InfiniteTimeSpan)
        {
            DisarmDeadline();
            return;
        }

        if (_customDeadline is { } custom)
        {
            if (!custom.IsCancellationRequested)
            {
                custom.CancelAfter(timeout);
            }
        }
        else if (ReferenceEquals(timeProvider, TimeProvider.System))
        {
            _cts.CancelAfter(timeout);
        }
        else
        {
            var created = new CancellationTokenSource(timeout, timeProvider);
            _customDeadline = created;
            _customDeadlineReg = created.Token.UnsafeRegister(CancelCallback, _cts);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _returned, 1) != 0)
        {
            return;
        }

        _shutdownReg.Dispose();
        _shutdownReg = default;
        _callerReg.Dispose();
        _callerReg = default;
        _customDeadlineReg.Dispose();
        _customDeadlineReg = default;

        var custom = _customDeadline;
        _customDeadline = null;
        custom?.Dispose();

        _shutdown = default;
        _caller = default;

        if (custom is not null || !_cts.TryReset())
        {
            _cts.Dispose();
            return;
        }

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
            _cts.Dispose();
        }
    }
}

namespace TerraBase.Features.Sql;

internal sealed class SqlTransactionDeadline(TimeProvider time) : IDisposable
{
    private readonly Lock _gate = new();
    private CancellationTokenSource _expiry = new(Timeout.InfiniteTimeSpan, time);
    private long _started;
    private TimeSpan? _budget;
    private int _dispatched;

    public TimeSpan? Remaining
    {
        get
        {
            lock (_gate)
            {
                return Left();
            }
        }
    }

    public bool Expired
    {
        get
        {
            lock (_gate)
            {
                return _dispatched == 0 && Left() == TimeSpan.Zero;
            }
        }
    }

    public CancellationToken Token
    {
        get
        {
            lock (_gate)
            {
                return _expiry.Token;
            }
        }
    }

    public void Start() => Limit(SqlLimits.MaxTransactionTime);

    public void Limit(TimeSpan remaining)
    {
        remaining = remaining < TimeSpan.Zero ? TimeSpan.Zero : remaining;
        lock (_gate)
        {
            if (_budget is null)
            {
                Renew();
                _started = time.GetTimestamp();
                _budget = remaining < SqlLimits.MaxTransactionTime ? remaining : SqlLimits.MaxTransactionTime;
            }
            else if (Left() is { } current && remaining < current)
            {
                _budget = time.GetElapsedTime(_started) + remaining;
            }
            else
            {
                return;
            }

            Arm();
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            (_budget, _dispatched) = (null, 0);
            Renew();
            _expiry.CancelAfter(Timeout.InfiniteTimeSpan);
        }
    }

    public IDisposable? Dispatch()
    {
        lock (_gate)
        {
            if (_expiry.IsCancellationRequested || Left() == TimeSpan.Zero)
            {
                return null;
            }

            _dispatched++;
            _expiry.CancelAfter(Timeout.InfiniteTimeSpan);
        }

        return new Dispatched(this);
    }

    public CancellationTokenSource Bound(CancellationToken cancellationToken) =>
        CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, Token);

    public CancellationTokenSource Bound(TimeSpan budget, CancellationToken cancellationToken)
    {
        var bounded = Bound(cancellationToken);
        bounded.CancelAfter(budget);
        return bounded;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _expiry.Dispose();
        }
    }

    private TimeSpan? Left() => _budget is { } budget
        ? TimeSpan.FromTicks(Math.Max(0, (budget - time.GetElapsedTime(_started)).Ticks)) : null;

    private void Renew()
    {
        if (_expiry.IsCancellationRequested)
        {
            _expiry = new CancellationTokenSource(Timeout.InfiniteTimeSpan, time);
        }
    }

    private void Arm()
    {
        if (_dispatched == 0 && Left() is { } left && !_expiry.IsCancellationRequested)
        {
            _expiry.CancelAfter(left);
        }
    }

    private void Settle()
    {
        lock (_gate)
        {
            _dispatched = Math.Max(0, _dispatched - 1);
            Arm();
        }
    }

    private sealed class Dispatched(SqlTransactionDeadline owner) : IDisposable
    {
        private SqlTransactionDeadline? _owner = owner;

        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Settle();
    }
}

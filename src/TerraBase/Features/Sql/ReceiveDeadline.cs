using TerraBase.Features.Qos;

namespace TerraBase.Features.Sql;

internal sealed class ReceiveDeadline : IDisposable
{
    private static readonly TimeSpan DrainBudget = TimeSpan.FromSeconds(2);

    private readonly Lock _gate = new();
    private readonly TimeProvider _time;
    private readonly Func<bool, Task> _expire;
    private readonly CancellationTokenSource _stop = new();
    private readonly CancellationTokenSource? _linked;
    private readonly ITimer _timer;
    private long _armedAt;
    private TimeSpan _budget;
    private bool _partial;
    private bool _armed;
    private bool _expired;
    private bool _disposed;

    public ReceiveDeadline(TimeProvider time, Func<bool, Task> expire, CancellationToken outerToken = default)
    {
        _time = time;
        _expire = expire;
        if (outerToken.CanBeCanceled)
        {
            _linked = CancellationTokenSource.CreateLinkedTokenSource(outerToken, _stop.Token);
        }

        _timer = time.CreateTimer(static state => ((ReceiveDeadline?)state)?.Fire(), this, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public CancellationToken Token => _linked?.Token ?? _stop.Token;

    public Task Expiry { get; private set; } = Task.CompletedTask;

    public void Arm(TimeSpan idle, (long Started, int Bytes)? progress = null)
    {
        lock (_gate)
        {
            if (_disposed || _expired)
            {
                return;
            }

            _armed = true;
            _partial = false;
            _armedAt = _time.GetTimestamp();
            _budget = idle;
            Expiry = Task.CompletedTask;
            if (progress is { } initial && !Advance(initial.Started, initial.Bytes))
            {
                _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                return;
            }

            _timer.Change(Due(), Timeout.InfiniteTimeSpan);
        }
    }

    public void ArmPartial(long started, int bytes)
    {
        lock (_gate)
        {
            if (_disposed || _expired)
            {
                return;
            }

            var budget = QosPolicy.MessageBudget(bytes);
            if (_armed && _partial && _armedAt == started)
            {
                if (budget > _budget)
                {
                    _budget = budget;
                }

                return;
            }

            _armed = true;
            _partial = true;
            _armedAt = started;
            _budget = budget;
            Expiry = Task.CompletedTask;
            _timer.Change(Due(), Timeout.InfiniteTimeSpan);
        }
    }

    public void Progress(long started, int bytes)
    {
        lock (_gate)
        {
            if (!_armed || _disposed || _expired)
            {
                return;
            }

            var wasPartial = _partial && _armedAt == started;
            if (!Advance(started, bytes))
            {
                _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                return;
            }

            if (!wasPartial)
            {
                _timer.Change(Due(), Timeout.InfiniteTimeSpan);
            }
        }
    }

    private bool Advance(long started, int bytes)
    {
        if (_time.GetElapsedTime(_armedAt, started) > _budget)
        {
            _armed = false;
            _expired = true;
            Expiry = ExpireAsync(_partial);
            return false;
        }

        _partial = true;
        _armedAt = started;
        _budget = QosPolicy.MessageBudget(bytes);
        return true;
    }

    private TimeSpan Due()
    {
        var remaining = _budget - _time.GetElapsedTime(_armedAt);
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    public bool Complete()
    {
        lock (_gate)
        {
            if (_expired)
            {
                return false;
            }

            if (!_armed)
            {
                return true;
            }

            _armed = false;
            _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            if (_partial && _budget - _time.GetElapsedTime(_armedAt) <= TimeSpan.Zero)
            {
                _expired = true;
                Expiry = ExpireAsync(true);
                return false;
            }

            return true;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _armed = false;
            _timer.Dispose();
            _linked?.Dispose();
            _stop.Dispose();
        }
    }

    private void Fire()
    {
        lock (_gate)
        {
            if (!_armed || _disposed || _expired)
            {
                return;
            }

            var remaining = _budget - _time.GetElapsedTime(_armedAt);
            if (remaining > TimeSpan.Zero)
            {
                _timer.Change(remaining, Timeout.InfiniteTimeSpan);
                return;
            }

            _armed = false;
            _expired = true;
            Expiry = ExpireAsync(_partial);
        }
    }

    private async Task ExpireAsync(bool partial)
    {
        await Task.Yield();
        try
        {
            await _expire(partial);
        }
        finally
        {
            lock (_gate)
            {
                if (!_disposed)
                {
                    _stop.CancelAfter(DrainBudget);
                }
            }
        }
    }
}

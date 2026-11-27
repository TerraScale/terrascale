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
    private long _scheduledAt;
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
                CancelTimerLocked();
                return;
            }

            ScheduleLocked();
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
            ScheduleLocked();
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
                CancelTimerLocked();
                return;
            }

            if (!wasPartial)
            {
                ScheduleLocked();
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

    private void ScheduleLocked()
    {
        var now = _time.GetTimestamp();
        var remaining = _budget - _time.GetElapsedTime(_armedAt, now);
        if (remaining <= TimeSpan.Zero)
        {
            remaining = TimeSpan.Zero;
        }

        var target = _budget > TimeSpan.Zero
            ? _armedAt + ToTimestampTicks(_budget)
            : now;
        if (target <= 0)
        {
            target = 1;
        }

        if (_scheduledAt == 0 || target < _scheduledAt)
        {
            _scheduledAt = target;
            _timer.Change(remaining, Timeout.InfiniteTimeSpan);
        }
    }

    private void CancelTimerLocked()
    {
        if (_scheduledAt != 0)
        {
            _scheduledAt = 0;
            _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }
    }

    private long ToTimestampTicks(TimeSpan duration) =>
        (long)(duration.Ticks * ((double)_time.TimestampFrequency / TimeSpan.TicksPerSecond));

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
            if (_partial && _budget - _time.GetElapsedTime(_armedAt) <= TimeSpan.Zero)
            {
                _expired = true;
                CancelTimerLocked();
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
            _scheduledAt = 0;
            _timer.Dispose();
            _linked?.Dispose();
            _stop.Dispose();
        }
    }

    private void Fire()
    {
        lock (_gate)
        {
            _scheduledAt = 0;
            if (!_armed || _disposed || _expired)
            {
                return;
            }

            var now = _time.GetTimestamp();
            var remaining = _budget - _time.GetElapsedTime(_armedAt, now);
            if (remaining > TimeSpan.Zero)
            {
                var target = _armedAt + ToTimestampTicks(_budget);
                _scheduledAt = target > 0 ? target : 1;
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

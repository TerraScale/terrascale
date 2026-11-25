using TerraBase.Features.Qos;

namespace TerraBase.Features.Sql;

internal sealed class ReceiveDeadline : IDisposable
{
    private static readonly TimeSpan DrainBudget = TimeSpan.FromSeconds(2);

    private readonly Lock _gate = new();
    private readonly TimeProvider _time;
    private readonly Func<bool, Task> _expire;
    private readonly CancellationTokenSource _stop = new();
    private readonly ITimer _timer;
    private long _armedAt;
    private TimeSpan _budget;
    private bool _partial;
    private bool _finished;
    private bool _disposed;

    public ReceiveDeadline(TimeProvider time, TimeSpan idle, Func<bool, Task> expire, (long Started, int Bytes)? progress = null)
    {
        _time = time;
        _expire = expire;
        _armedAt = time.GetTimestamp();
        _budget = idle;
        if (progress is { } initial)
        {
            _ = Advance(initial.Started, initial.Bytes);
        }

        _timer = time.CreateTimer(_ => Fire(), null, _finished ? Timeout.InfiniteTimeSpan : Due(), Timeout.InfiniteTimeSpan);
    }

    public CancellationToken Token => _stop.Token;

    public Task Expiry { get; private set; } = Task.CompletedTask;

    public void Progress(long started, int bytes)
    {
        lock (_gate)
        {
            if (_finished)
            {
                return;
            }

            _timer.Change(Advance(started, bytes) ? Due() : Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }
    }

    private bool Advance(long started, int bytes)
    {
        if (_time.GetElapsedTime(_armedAt, started) > _budget)
        {
            _finished = true;
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
            if (_finished)
            {
                return false;
            }

            _finished = true;
            _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            if (_partial && _budget - _time.GetElapsedTime(_armedAt) <= TimeSpan.Zero)
            {
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
            _timer.Dispose();
            _stop.Dispose();
        }
    }

    private void Fire()
    {
        bool partial;
        lock (_gate)
        {
            if (_finished)
            {
                return;
            }

            var remaining = _budget - _time.GetElapsedTime(_armedAt);
            if (remaining > TimeSpan.Zero)
            {
                _timer.Change(remaining, Timeout.InfiniteTimeSpan);
                return;
            }

            _finished = true;
            partial = _partial;
            Expiry = ExpireAsync(partial);
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

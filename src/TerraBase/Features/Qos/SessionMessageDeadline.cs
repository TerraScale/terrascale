using TerraBase.Features.Sql;

namespace TerraBase.Features.Qos;

internal sealed class SessionMessageDeadline : IDisposable
{
    private readonly Lock _gate = new();
    private readonly TimeProvider _time;
    private readonly CancellationTokenSource _stop = new();
    private readonly ITimer _timer;
    private bool _completed;
    private bool _expired;
    private long? _started;
    private long _bytes;
    private long _armedAt;
    private TimeSpan _budget;

    public SessionMessageDeadline(TimeProvider time, TimeSpan? transaction, bool executing = false)
    {
        _time = time;
        IsTransaction = transaction is not null;
        var idle = transaction is { } remaining && remaining < SqlLimits.MaxTransactionTime ? remaining
            : transaction is not null ? SqlLimits.MaxTransactionTime : QosSessions.IdleTimeout;
        _armedAt = time.GetTimestamp();
        _budget = executing ? Timeout.InfiniteTimeSpan : idle;
        _timer = time.CreateTimer(_ => Expire(), null, _budget, Timeout.InfiniteTimeSpan);
    }

    public CancellationToken Token => _stop.Token;

    public bool IsCancellationRequested => _stop.IsCancellationRequested;

    public bool IsTransaction { get; private set; }

    public bool IsPartial => _started is not null;

    public void Progress(int bytes, long? started = null)
    {
        lock (_gate)
        {
            if (_expired || _completed)
            {
                return;
            }

            _started ??= started ?? _time.GetTimestamp();
            _bytes += bytes;
            var remaining = QosSessions.MessageTimeout + TimeSpan.FromSeconds((double)_bytes / QosSessions.MinimumMessageBytesPerSecond)
                - _time.GetElapsedTime(_started.Value);
            Arm(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero);
        }
    }

    public void Resume(TimeSpan? transaction)
    {
        lock (_gate)
        {
            if (!_expired && !_completed && _started is null)
            {
                IsTransaction = transaction is not null;
                var idle = transaction is { } remaining && remaining < SqlLimits.MaxTransactionTime ? remaining
                    : transaction is not null ? SqlLimits.MaxTransactionTime : QosSessions.IdleTimeout;
                Arm(idle);
            }
        }
    }

    public void Complete()
    {
        lock (_gate)
        {
            if (_expired)
            {
                throw new OperationCanceledException(Token);
            }

            _completed = true;
            _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _completed = true;
            _timer.Dispose();
        }

        _stop.Dispose();
    }

    private void Arm(TimeSpan budget)
    {
        _armedAt = _time.GetTimestamp();
        _budget = budget;
        _timer.Change(budget, Timeout.InfiniteTimeSpan);
    }

    private void Expire()
    {
        lock (_gate)
        {
            if (!_completed && _budget != Timeout.InfiniteTimeSpan)
            {
                var remaining = _budget - _time.GetElapsedTime(_armedAt);
                if (remaining > TimeSpan.Zero)
                {
                    _timer.Change(remaining, Timeout.InfiniteTimeSpan);
                    return;
                }

                _expired = true;
                _stop.Cancel();
            }
        }
    }
}

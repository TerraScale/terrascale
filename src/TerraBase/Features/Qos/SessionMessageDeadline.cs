using TerraBase.Features.Sql;

namespace TerraBase.Features.Qos;

internal sealed class SessionMessageDeadline : IDisposable
{
    private readonly Lock _gate = new();
    private readonly TimeProvider _time;
    private readonly CancellationTokenSource _stop = new();
    private readonly ITimer _timer;
    private bool _completed;
    private bool _disposed;
    private bool _expired;
    private bool _cancelling;
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
        _timer = time.CreateTimer(_ => Expire(), null, _budget == TimeSpan.Zero ? Timeout.InfiniteTimeSpan : _budget, Timeout.InfiniteTimeSpan);
        if (_budget == TimeSpan.Zero)
        {
            _expired = true;
            _stop.Cancel();
        }
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

            var arrived = started ?? _time.GetTimestamp();
            if (_started is null && _budget != Timeout.InfiniteTimeSpan && _time.GetElapsedTime(_armedAt, arrived) >= _budget)
            {
                _expired = true;
            }
            else
            {
                _started ??= arrived;
                _bytes += bytes;
                var remaining = QosSessions.MessageTimeout + TimeSpan.FromSeconds((double)_bytes / QosSessions.MinimumMessageBytesPerSecond)
                    - _time.GetElapsedTime(_started.Value);
                Arm(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero);
            }
        }

        CancelExpired();
    }

    public void ExpectAnswer(bool expected)
    {
        lock (_gate)
        {
            if (!_expired && !_disposed)
            {
                _completed = !expected;
                _started = null;
                _bytes = 0;
                Arm(expected ? QosSessions.AnswerTimeout : Timeout.InfiniteTimeSpan);
            }
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

        CancelExpired();
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
            _completed = _disposed = true;
            _timer.Dispose();
            if (!_cancelling)
            {
                _stop.Dispose();
            }
        }
    }

    private void Arm(TimeSpan budget)
    {
        _armedAt = _time.GetTimestamp();
        _budget = budget;
        if (budget == TimeSpan.Zero)
        {
            _expired = true;
        }
        else
        {
            _timer.Change(budget, Timeout.InfiniteTimeSpan);
        }
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
            }
        }

        CancelExpired();
    }

    private void CancelExpired()
    {
        lock (_gate)
        {
            if (!_expired || _completed || _cancelling || _stop.IsCancellationRequested)
            {
                return;
            }

            _cancelling = true;
        }

        try
        {
            _stop.Cancel();
        }
        finally
        {
            lock (_gate)
            {
                _cancelling = false;
                if (_completed)
                {
                    _stop.Dispose();
                }
            }
        }
    }
}

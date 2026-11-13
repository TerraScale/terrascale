using TerraBase.Features.Sql;

namespace TerraBase.Features.Qos;

internal sealed class SessionMessageDeadline : IDisposable
{
    private readonly TimeProvider _time;
    private readonly CancellationTokenSource _stop;
    private readonly CancellationTokenSource? _transaction;
    private long? _started;
    private long _bytes;

    public SessionMessageDeadline(TimeProvider time, SqlTransactionDeadline? transaction)
    {
        _time = time;
        _stop = new CancellationTokenSource(transaction is null ? QosSessions.IdleTimeout : Timeout.InfiniteTimeSpan, time);
        _transaction = transaction?.Bound(_stop.Token);
    }

    public CancellationToken Token => (_transaction ?? _stop).Token;

    public bool IsCancellationRequested => Token.IsCancellationRequested;

    public bool IsPartial => _started is not null;

    public void Progress(int bytes, long? started = null)
    {
        _started ??= started ?? _time.GetTimestamp();
        _bytes += bytes;
        var remaining = QosSessions.MessageTimeout + TimeSpan.FromSeconds((double)_bytes / QosSessions.MinimumMessageBytesPerSecond)
            - _time.GetElapsedTime(_started.Value);
        _stop.CancelAfter(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero);
    }

    public void Dispose()
    {
        _transaction?.Dispose();
        _stop.Dispose();
    }
}

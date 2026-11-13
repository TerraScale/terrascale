namespace TerraBase.Features.Qos;

internal sealed class SessionMessageDeadline(TimeProvider time, TimeSpan? transaction) : IDisposable
{
    private readonly long _waiting = time.GetTimestamp();
    private long? _started;
    private long _bytes;
    private readonly CancellationTokenSource _stop = new(transaction ?? QosSessions.IdleTimeout, time);

    public CancellationToken Token => _stop.Token;
    public bool IsCancellationRequested => _stop.IsCancellationRequested;
    public bool IsPartial => _started is not null;

    public void Progress(int bytes, long? started = null)
    {
        _started ??= started ?? time.GetTimestamp();
        _bytes += bytes;
        var remaining = QosSessions.MessageTimeout + TimeSpan.FromSeconds((double)_bytes / QosSessions.MinimumMessageBytesPerSecond)
            - time.GetElapsedTime(_started.Value);
        if (transaction is { } budget)
        {
            var left = budget - time.GetElapsedTime(_waiting);
            remaining = left < remaining ? left : remaining;
        }

        _stop.CancelAfter(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero);
    }

    public void Dispose() => _stop.Dispose();
}

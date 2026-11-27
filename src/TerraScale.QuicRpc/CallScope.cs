namespace TerraScale.QuicRpc;

internal sealed class CallScope : IDisposable
{
    private readonly CancellationTokenSource _deadline;
    private readonly CancellationTokenSource _linked;
    private readonly CancellationToken _shutdown;

    public CallScope(TimeSpan timeout, TimeProvider timeProvider, CancellationToken shutdown, CancellationToken caller)
    {
        _deadline = new CancellationTokenSource(timeout, timeProvider);
        _linked = CancellationTokenSource.CreateLinkedTokenSource(caller, shutdown, _deadline.Token);
        _shutdown = shutdown;
        Caller = caller;
    }

    public CancellationToken Caller { get; }

    public CancellationToken Token => _linked.Token;

    public void DisarmDeadline() => _deadline.CancelAfter(Timeout.InfiniteTimeSpan);

    public QuicFault Interrupted() =>
        _shutdown.IsCancellationRequested ? new TransportShutdown("The QUIC transport is shutting down.")
        : _deadline.IsCancellationRequested ? new DeadlineExceeded("The QUIC operation exceeded its deadline.")
        : new LocalAbort("The QUIC operation was canceled.");

    public void Dispose()
    {
        _linked.Dispose();
        _deadline.Dispose();
    }
}

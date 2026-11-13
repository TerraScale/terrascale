namespace TerraBase.Features.Qos;

internal sealed class SessionPreauthentication : IDisposable
{
    private readonly IDisposable _allowance;
    private readonly CancellationTokenSource _deadline;
    private readonly CancellationToken _outer;

    public SessionPreauthentication(IDisposable allowance, TimeSpan deadline, CancellationToken cancellationToken)
    {
        _allowance = allowance;
        _outer = cancellationToken;
        _deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _deadline.CancelAfter(deadline);
    }

    public CancellationToken Token => _deadline.Token;

    public bool Expired => _deadline.IsCancellationRequested && !_outer.IsCancellationRequested;

    public void Dispose()
    {
        _allowance.Dispose();
        _deadline.Dispose();
    }
}

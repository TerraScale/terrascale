namespace TerraBase.Features.Sql;

internal sealed class SqlTransactionDeadline(TimeProvider time)
{
    private long _started;
    private TimeSpan? _budget;

    public TimeSpan? Remaining => _budget is { } budget
        ? TimeSpan.FromTicks(Math.Max(0, (budget - time.GetElapsedTime(_started)).Ticks)) : null;

    public void Start() => Limit(SqlLimits.MaxTransactionTime);

    public void Limit(TimeSpan remaining)
    {
        remaining = remaining < TimeSpan.Zero ? TimeSpan.Zero : remaining;
        if (_budget is null)
        {
            _started = time.GetTimestamp();
            _budget = remaining < SqlLimits.MaxTransactionTime ? remaining : SqlLimits.MaxTransactionTime;
        }
        else if (Remaining is { } current && remaining < current)
        {
            _budget = time.GetElapsedTime(_started) + remaining;
        }
    }

    public void Clear() => _budget = null;

    public CancellationTokenSource Bound(TimeSpan budget, CancellationToken cancellationToken)
    {
        var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bounded.CancelAfter(Remaining is { } remaining && remaining < budget ? remaining : budget);
        return bounded;
    }
}

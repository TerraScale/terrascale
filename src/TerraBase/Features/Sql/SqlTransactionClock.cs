namespace TerraBase.Features.Sql;

internal sealed class SqlTransactionClock(TimeProvider time)
{
    private long? _started;
    private TimeSpan _budget = SqlLimits.MaxTransactionTime;

    public TimeSpan? Remaining => _started is { } started
        ? TimeSpan.FromTicks(Math.Max(0, (_budget - time.GetElapsedTime(started)).Ticks)) : null;

    public void Start(long? started = null)
    {
        _started ??= started ?? time.GetTimestamp();
    }

    public void Limit(TimeSpan remaining)
    {
        Start();
        if (remaining < Remaining)
        {
            _budget = time.GetElapsedTime(_started!.Value) + (remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero);
        }
    }

    public void End()
    {
        _started = null;
        _budget = SqlLimits.MaxTransactionTime;
    }
}

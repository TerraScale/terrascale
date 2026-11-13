namespace TerraBase.Features.Sql;

internal static class SqlLimits
{
    public static readonly TimeSpan MaxRequestTime = TimeSpan.FromSeconds(5);

    public static readonly TimeSpan ForwardedRequestTime = TimeSpan.FromSeconds(15);

    public static readonly TimeSpan MaxTransactionTime = TimeSpan.FromSeconds(5);

    public static TimeSpan Remaining(TimeProvider time, long opened) =>
        TimeSpan.FromTicks(Math.Max(0, (MaxTransactionTime - time.GetElapsedTime(opened)).Ticks));
}

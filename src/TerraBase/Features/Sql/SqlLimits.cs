namespace TerraBase.Features.Sql;

internal static class SqlLimits
{
    public static readonly TimeSpan MaxRequestTime = TimeSpan.FromSeconds(5);

    public static readonly TimeSpan MaxTransactionTime = TimeSpan.FromSeconds(5);

    public const string TransactionClock = "sql-transaction";
}

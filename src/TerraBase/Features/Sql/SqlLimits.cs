namespace TerraBase.Features.Sql;

/// <summary>Resource bounds shared by SQL sessions and their transports.</summary>
internal static class SqlLimits
{
    /// <summary>One request's SQL statements share this deadline, including autocommit statements.</summary>
    public static readonly TimeSpan MaxRequestTime = TimeSpan.FromSeconds(5);
}

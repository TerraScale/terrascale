namespace TerraScale.DotNet.TerraBase;

public sealed partial class TerraBaseProjectClient
{
    /// <summary>Reads regional best-effort analytics. Range: 1h, 24h (default), 7d or 30d; databaseId is a catalog ULID filter. Requires databases:read.</summary>
    public async Task<TerraBaseProjectAnalytics> GetAnalyticsAsync(string? range = null, string? databaseId = null, string? region = null, CancellationToken cancellationToken = default) =>
        (await connection.SendAsync(HttpMethod.Get, path + "/analytics" + AnalyticsQuery(range, databaseId, region),
            null, Json.TerraBaseProjectAnalytics, null, cancellationToken).ConfigureAwait(false)).Value;

    /// <summary>Reads regional replication observations; null health fields mean unknown or stale. Same ranges and filters as analytics. Requires databases:read.</summary>
    public async Task<TerraBaseProjectReplication> GetReplicationAsync(string? range = null, string? databaseId = null, string? region = null, CancellationToken cancellationToken = default) =>
        (await connection.SendAsync(HttpMethod.Get, path + "/replication" + AnalyticsQuery(range, databaseId, region),
            null, Json.TerraBaseProjectReplication, null, cancellationToken).ConfigureAwait(false)).Value;

    internal static string AnalyticsQuery(string? range, string? databaseId, string? region) =>
        TerraBaseConnection.Query(("range", range), ("database", databaseId), ("region", region));
}

public sealed partial class TerraBaseDatabase
{
    /// <summary>Reads this database name's regional analytics. Range: 1h, 24h (default), 7d or 30d; optional observing region. Requires databases:read.</summary>
    public async Task<TerraBaseProjectAnalytics> GetAnalyticsAsync(string? range = null, string? region = null, CancellationToken cancellationToken = default) =>
        (await connection.SendAsync(HttpMethod.Get, path + "/analytics" + TerraBaseProjectClient.AnalyticsQuery(range, null, region),
            null, Json.TerraBaseProjectAnalytics, null, cancellationToken).ConfigureAwait(false)).Value;

    /// <summary>Reads this database's replication observations; unknown/stale health fields stay null. Requires databases:read.</summary>
    public async Task<TerraBaseProjectReplication> GetReplicationAsync(string? range = null, string? region = null, CancellationToken cancellationToken = default) =>
        (await connection.SendAsync(HttpMethod.Get, path + "/replication" + TerraBaseProjectClient.AnalyticsQuery(range, null, region),
            null, Json.TerraBaseProjectReplication, null, cancellationToken).ConfigureAwait(false)).Value;
}

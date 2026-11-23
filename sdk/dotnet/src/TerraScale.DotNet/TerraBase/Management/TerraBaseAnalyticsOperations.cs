namespace TerraScale.DotNet.TerraBase;

public sealed partial class TerraBaseProjectClient
{
    public async Task<TerraBaseProjectAnalytics> GetAnalyticsAsync(string? range = null, string? databaseId = null, string? region = null, CancellationToken cancellationToken = default) =>
        (await connection.SendAsync(HttpMethod.Get, path + "/analytics" + AnalyticsQuery(range, databaseId, region),
            null, Json.TerraBaseProjectAnalytics, null, cancellationToken).ConfigureAwait(false)).Value;

    internal static string AnalyticsQuery(string? range, string? databaseId, string? region) =>
        TerraBaseConnection.Query(("range", range), ("database", databaseId), ("region", region));
}

public sealed partial class TerraBaseDatabase
{
    public async Task<TerraBaseProjectAnalytics> GetAnalyticsAsync(string? range = null, string? region = null, CancellationToken cancellationToken = default) =>
        (await connection.SendAsync(HttpMethod.Get, path + "/analytics" + TerraBaseProjectClient.AnalyticsQuery(range, null, region),
            null, Json.TerraBaseProjectAnalytics, null, cancellationToken).ConfigureAwait(false)).Value;

}

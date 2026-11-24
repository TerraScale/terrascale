using System.Globalization;

namespace TerraScale.DotNet.TerraBase;

public sealed partial class TerraBaseProjectClient
{
    private readonly TerraBaseConnection connection;
    private readonly string path;
    private static readonly TerraBaseJsonContext Json = TerraBaseJsonContext.Wire;

    internal TerraBaseProjectClient(TerraBaseConnection connection, string path)
    {
        this.connection = connection;
        this.path = path;
    }

    public async Task<TerraBaseManagedDatabase> CreateDatabaseAsync(string name, TerraBaseDatabasePlacement? placement = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        var body = TerraBaseConnection.Json(new TerraBaseCreateDatabaseRequest(name, placement?.Regions, placement?.AllRegions), Json.TerraBaseCreateDatabaseRequest);
        return (await connection.SendAsync(HttpMethod.Post, path + "/databases", body, Json.TerraBaseManagedDatabase, null, cancellationToken).ConfigureAwait(false)).Value;
    }

    public async Task<TerraBaseManagedDatabase> CreateDatabaseAsync(string name, TerraBaseDatabasePlacement? placement, TerraBaseConsistencyMode defaultConsistency,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        var body = TerraBaseConnection.Json(new TerraBaseCreateDatabaseRequest(name, placement?.Regions, placement?.AllRegions, defaultConsistency), Json.TerraBaseCreateDatabaseRequest);
        return (await connection.SendAsync(HttpMethod.Post, path + "/databases", body, Json.TerraBaseManagedDatabase, null, cancellationToken).ConfigureAwait(false)).Value;
    }

    public async Task<TerraBaseManagedDatabase> SetDatabaseConsistencyAsync(string databaseName, TerraBaseConsistencyMode defaultConsistency, CancellationToken cancellationToken = default)
    {
        var body = TerraBaseConnection.Json(new TerraBaseAgentDatabaseRequest(DefaultConsistency: defaultConsistency), Json.TerraBaseAgentDatabaseRequest);
        return (await connection.SendAsync(HttpMethod.Patch, DatabasePath(databaseName), body, Json.TerraBaseManagedDatabase, null, cancellationToken).ConfigureAwait(false)).Value;
    }

    public async Task<TerraBasePage<TerraBaseManagedDatabase>> ListDatabasesAsync(string? after = null, int? limit = null, CancellationToken cancellationToken = default) =>
        (await connection.SendAsync(HttpMethod.Get, path + "/databases" + TerraBaseConnection.Query(("after", after), ("limit", TerraBaseConnection.Number(limit))),
            null, Json.TerraBasePageTerraBaseManagedDatabase, null, cancellationToken).ConfigureAwait(false)).Value;

    public async Task<TerraBaseManagedDatabase> GetDatabaseAsync(string databaseName, CancellationToken cancellationToken = default) =>
        (await connection.SendAsync(HttpMethod.Get, DatabasePath(databaseName), null, Json.TerraBaseManagedDatabase, null, cancellationToken).ConfigureAwait(false)).Value;

    public Task DeleteDatabaseAsync(string databaseName, CancellationToken cancellationToken = default) =>
        connection.SendAsync(HttpMethod.Delete, DatabasePath(databaseName), null, null, cancellationToken);

    public async Task<TerraBaseProjectUsage> GetUsageAsync(string? granularity = null, DateTimeOffset? from = null, DateTimeOffset? to = null, CancellationToken cancellationToken = default) =>
        (await connection.SendAsync(HttpMethod.Get, path + "/usage" + UsageQuery(granularity, from, to), null, Json.TerraBaseProjectUsage, null, cancellationToken).ConfigureAwait(false)).Value;

    internal static string UsageQuery(string? granularity, DateTimeOffset? from, DateTimeOffset? to) =>
        TerraBaseConnection.Query(("granularity", granularity), ("from", Instant(from)), ("to", Instant(to)));

    private static string? Instant(DateTimeOffset? instant) => instant?.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffffzzz", CultureInfo.InvariantCulture);

    private string DatabasePath(string databaseName) => path + "/databases/" + TerraScalePaths.Segment(databaseName, nameof(databaseName));
}

internal sealed record TerraBaseCreateDatabaseRequest(string Name, IReadOnlyList<string>? Regions, bool? AllRegions, TerraBaseConsistencyMode? DefaultConsistency = null);

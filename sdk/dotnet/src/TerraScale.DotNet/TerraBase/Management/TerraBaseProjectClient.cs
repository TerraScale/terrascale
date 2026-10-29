using System.Globalization;

namespace TerraScale.DotNet.TerraBase;

/// <summary>
/// Canonical project-token database lifecycle and usage. Obtain from <see cref="TerraBaseDocumentsClient.Project"/>.
/// Requires databases:read for reads and databases:write for mutations. Database routes accept names or immutable ULIDs.
/// </summary>
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

    /// <summary>
    /// Creates a database managed in the called region, or the specified home. Sent once; a regional pin may refuse a different home.
    /// A writable database requires at least two regions. Omitting <paramref name="replicaRegions"/> uses every other current serving region;
    /// an explicit list must include at least one region other than the home. An empty list returns 400 placement_needs_two_regions.
    /// <paramref name="allRegions"/> true copies it to every serving region, including regions that join later; it cannot be combined with <paramref name="replicaRegions"/>.
    /// </summary>
    public async Task<TerraBaseManagedDatabase> CreateDatabaseAsync(string name, string? homeRegion = null,
        IReadOnlyList<string>? replicaRegions = null, bool? allRegions = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        var body = TerraBaseConnection.Json(new TerraBaseDatabaseRegionsRequest(name, homeRegion, replicaRegions, allRegions), Json.TerraBaseDatabaseRegionsRequest);
        return (await connection.SendAsync(HttpMethod.Post, path + "/databases", body, Json.TerraBaseManagedDatabase, null, cancellationToken).ConfigureAwait(false)).Value;
    }

    /// <summary>Lists databases in creation order; limit is 1–100, default 20. Pass Next as after.</summary>
    public async Task<TerraBasePage<TerraBaseManagedDatabase>> ListDatabasesAsync(string? after = null, int? limit = null, CancellationToken cancellationToken = default) =>
        (await connection.SendAsync(HttpMethod.Get, path + "/databases" + TerraBaseConnection.Query(("after", after), ("limit", TerraBaseConnection.Number(limit))),
            null, Json.TerraBasePageTerraBaseManagedDatabase, null, cancellationToken).ConfigureAwait(false)).Value;

    /// <summary>Reads a database by name, including the answering region and current placement.</summary>
    public async Task<TerraBaseManagedDatabase> GetDatabaseAsync(string databaseName, CancellationToken cancellationToken = default) =>
        (await connection.SendAsync(HttpMethod.Get, DatabasePath(databaseName), null, Json.TerraBaseManagedDatabase, null, cancellationToken).ConfigureAwait(false)).Value;

    /// <summary>
    /// Deletes directly in the home region without a preceding placement update. Permanent purge authority removes every replica,
    /// including all-regions placement. A 204 response records logical deletion and local cleanup; remote erasure is retried
    /// durably, not guaranteed synchronous. Branches remain independent. Sent once.
    /// </summary>
    public Task DeleteDatabaseAsync(string databaseName, CancellationToken cancellationToken = default) =>
        connection.SendAsync(HttpMethod.Delete, DatabasePath(databaseName), null, null, cancellationToken);

    /// <summary>
    /// Replaces the complete replica list in the home region and turns all-regions placement off. A writable database needs
    /// at least one replica besides the home. An empty list can temporarily make document writes return 409 placement_needs_two_regions;
    /// it is not required for deletion and does not provide supported single-region placement. Home and name cannot change. Sent once.
    /// </summary>
    public async Task<TerraBaseManagedDatabase> SetDatabaseRegionsAsync(string databaseName, IReadOnlyList<string> replicaRegions, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(replicaRegions);
        return await SetPlacementAsync(databaseName, new TerraBaseDatabaseRegionsRequest(null, null, replicaRegions, null), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Copies the database to every serving region, including regions that join later, and clears the replica list. Sent once.</summary>
    public Task<TerraBaseManagedDatabase> SetDatabaseAllRegionsAsync(string databaseName, CancellationToken cancellationToken = default) =>
        SetPlacementAsync(databaseName, new TerraBaseDatabaseRegionsRequest(null, null, null, true), cancellationToken);

    /// <summary>
    /// Reads best-effort dashboard usage, not billing. Granularity is hour (default) or day; from inclusive and to
    /// exclusive are rounded to UTC buckets. More than 100 databases or an expensive query answers 400, never zero usage.
    /// </summary>
    public async Task<TerraBaseProjectUsage> GetUsageAsync(string? granularity = null, DateTimeOffset? from = null, DateTimeOffset? to = null, CancellationToken cancellationToken = default) =>
        (await connection.SendAsync(HttpMethod.Get, path + "/usage" + UsageQuery(granularity, from, to), null, Json.TerraBaseProjectUsage, null, cancellationToken).ConfigureAwait(false)).Value;

    internal static string UsageQuery(string? granularity, DateTimeOffset? from, DateTimeOffset? to) =>
        TerraBaseConnection.Query(("granularity", granularity), ("from", Instant(from)), ("to", Instant(to)));

    private static string? Instant(DateTimeOffset? instant) => instant?.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffffzzz", CultureInfo.InvariantCulture);

    private string DatabasePath(string databaseName) => path + "/databases/" + TerraBaseConnection.Segment(databaseName, nameof(databaseName));

    private async Task<TerraBaseManagedDatabase> SetPlacementAsync(string databaseName, TerraBaseDatabaseRegionsRequest request, CancellationToken cancellationToken)
    {
        var body = TerraBaseConnection.Json(request, Json.TerraBaseDatabaseRegionsRequest);
        return (await connection.SendAsync(HttpMethod.Patch, DatabasePath(databaseName), body, Json.TerraBaseManagedDatabase, null, cancellationToken).ConfigureAwait(false)).Value;
    }
}

internal sealed record TerraBaseDatabaseRegionsRequest(string? Name, string? HomeRegion, IReadOnlyList<string>? ReplicaRegions, bool? AllRegions);

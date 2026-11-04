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
    /// Creates a database in the called region, or in <paramref name="homeRegion"/>. A database lives in exactly one region.
    /// Sent once; a regional pin may refuse a different region.
    /// </summary>
    public async Task<TerraBaseManagedDatabase> CreateDatabaseAsync(string name, string? homeRegion = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        var body = TerraBaseConnection.Json(new TerraBaseCreateDatabaseRequest(name, homeRegion), Json.TerraBaseCreateDatabaseRequest);
        return (await connection.SendAsync(HttpMethod.Post, path + "/databases", body, Json.TerraBaseManagedDatabase, null, cancellationToken).ConfigureAwait(false)).Value;
    }

    /// <summary>Lists databases in creation order; limit is 1–100, default 20. Pass Next as after.</summary>
    public async Task<TerraBasePage<TerraBaseManagedDatabase>> ListDatabasesAsync(string? after = null, int? limit = null, CancellationToken cancellationToken = default) =>
        (await connection.SendAsync(HttpMethod.Get, path + "/databases" + TerraBaseConnection.Query(("after", after), ("limit", TerraBaseConnection.Number(limit))),
            null, Json.TerraBasePageTerraBaseManagedDatabase, null, cancellationToken).ConfigureAwait(false)).Value;

    /// <summary>Reads a database by name, including the answering region and its home region.</summary>
    public async Task<TerraBaseManagedDatabase> GetDatabaseAsync(string databaseName, CancellationToken cancellationToken = default) =>
        (await connection.SendAsync(HttpMethod.Get, DatabasePath(databaseName), null, Json.TerraBaseManagedDatabase, null, cancellationToken).ConfigureAwait(false)).Value;

    /// <summary>
    /// Deletes the database in its region. A 204 response records logical deletion and local cleanup; remote erasure is
    /// retried durably, not guaranteed synchronous. Branches remain independent. Sent once.
    /// </summary>
    public Task DeleteDatabaseAsync(string databaseName, CancellationToken cancellationToken = default) =>
        connection.SendAsync(HttpMethod.Delete, DatabasePath(databaseName), null, null, cancellationToken);

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

}

internal sealed record TerraBaseCreateDatabaseRequest(string Name, string? HomeRegion);

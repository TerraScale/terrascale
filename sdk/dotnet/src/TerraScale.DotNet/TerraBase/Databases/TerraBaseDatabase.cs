namespace TerraScale.DotNet.TerraBase;

public sealed record TerraBaseDatabaseClientOptions
{
    public Uri? BaseAddress { get; init; }

    public ITerraBaseEndpointResolver? EndpointResolver { get; init; }

    public string? Region { get; init; }

    public string? ApiKey { get; init; }

    public Func<CancellationToken, ValueTask<string>>? AccessTokenProvider { get; init; }

    public override string ToString() => $"TerraBase client options for {BaseAddress}";
}

public sealed class TerraBaseDatabaseClient
{
    private readonly TerraBaseConnection connection;

    public TerraBaseDatabaseClient(HttpClient httpClient, TerraBaseDatabaseClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        connection = new TerraBaseConnection(httpClient, TerraBaseRegion.OriginOrPublic(options.BaseAddress, options.EndpointResolver), options.EndpointResolver, options.ApiKey, nameof(options.ApiKey), TerraBaseConnection.RequestDeadline, TerraBaseConnection.DefaultMaxResponseBytes,
            options.AccessTokenProvider, anonymousAllowed: true, region: options.Region);
    }

    internal TerraBaseDatabaseClient(TerraBaseConnection connection) => this.connection = connection;

    public TerraBaseProjectClient Project(string projectId) =>
        new(connection, $"/v1/projects/{TerraScalePaths.Segment(projectId, nameof(projectId))}");

    public TerraBaseDatabase Database(string projectId, string databaseName) =>
        new(connection, $"/v1/projects/{TerraScalePaths.Segment(projectId, nameof(projectId))}/databases/{TerraScalePaths.Segment(databaseName, nameof(databaseName))}",
            $"/v1/projects/{TerraScalePaths.Segment(projectId, nameof(projectId))}");
}

public sealed record TerraBaseDatabaseInfo(string Id, string ProjectId, string Name)
{
    public IReadOnlyList<string> Regions { get; init; } = [];

    public long PlacementVersion { get; init; }

    public bool AllRegions { get; init; }

    public TerraBaseConsistencyMode? DefaultConsistency { get; init; }
}

public sealed partial class TerraBaseDatabase
{
    private static readonly TerraBaseJsonContext Json = TerraBaseJsonContext.Wire;

    private readonly TerraBaseConnection connection;
    private readonly string path;
    private readonly string projectPath;

    internal TerraBaseDatabase(TerraBaseConnection connection, string path, string projectPath)
    {
        this.connection = connection;
        this.path = path;
        this.projectPath = projectPath;
    }

    public async Task<TerraBaseDatabaseInfo> CreateAsync(CancellationToken cancellationToken = default) =>
        (await connection.SendAsync(HttpMethod.Put, path, null, Json.TerraBaseDatabaseInfo, null, cancellationToken).ConfigureAwait(false)).Value;

    public Task DeleteAsync(CancellationToken cancellationToken = default) =>
        connection.SendAsync(HttpMethod.Delete, path, null, null, cancellationToken);

    public async Task<TerraBaseDatabaseUsage> GetUsageAsync(string? granularity = null, DateTimeOffset? from = null, DateTimeOffset? to = null, CancellationToken cancellationToken = default) =>
        (await connection.SendAsync(HttpMethod.Get, path + "/usage" + TerraBaseProjectClient.UsageQuery(granularity, from, to),
            null, Json.TerraBaseDatabaseUsage, null, cancellationToken).ConfigureAwait(false)).Value;

    public async Task<TerraBaseDatabaseRegions> GetRegionsAsync(CancellationToken cancellationToken = default) =>
        (await connection.SendAsync(HttpMethod.Get, path + "/regions", null, Json.TerraBaseDatabaseRegions, null, cancellationToken).ConfigureAwait(false)).Value;

    public async Task<TerraBaseManagedDatabase> BranchAsync(string name, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        return (await connection.SendAsync(HttpMethod.Post, path + "/branches", TerraBaseConnection.Json(new RequestBody { Name = name }, Json.RequestBody),
            Json.TerraBaseManagedDatabase, null, cancellationToken).ConfigureAwait(false)).Value;
    }

    public async Task<TerraBaseManagedDatabase> BranchIfIdAsync(string name, string expectedId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        TerraScaleIds.Validate(expectedId);
        return (await connection.SendAsync(HttpMethod.Post, path + "/branches/if-id/" + Uri.EscapeDataString(expectedId),
            TerraBaseConnection.Json(new RequestBody { Name = name }, Json.RequestBody),
            Json.TerraBaseManagedDatabase, null, cancellationToken).ConfigureAwait(false)).Value;
    }

    public Task DeleteIfIdAsync(string expectedId, CancellationToken cancellationToken = default)
    {
        TerraScaleIds.Validate(expectedId);
        return connection.SendAsync(HttpMethod.Delete, path + "/if-id/" + Uri.EscapeDataString(expectedId), null, null, cancellationToken);
    }
}

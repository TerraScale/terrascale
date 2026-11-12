namespace TerraScale.DotNet.TerraBase;

/// <summary>Configures a <see cref="TerraBaseDatabaseClient"/>.</summary>
public sealed record TerraBaseDatabaseClientOptions
{
    /// <summary>
    /// HTTPS origin of the TerraBase client listener, for example <c>https://api.terrascale.tech</c>, or the plain-HTTP
    /// origin of its private listener on NetBird <c>100.64.0.0/10</c> addresses or loopback, for example
    /// <c>http://100.64.0.1:8081</c>.
    /// </summary>
    public Uri? BaseAddress { get; init; }

    /// <summary>Resolves the HTTPS client origin per request. Set this or BaseAddress, exclusively.</summary>
    public ITerraBaseEndpointResolver? EndpointResolver { get; init; }

    /// <summary>
    /// A region id such as <c>gru</c>: every request is pinned to that region (<c>X-TerraScale-Region-Pin</c>) and, unless
    /// BaseAddress or EndpointResolver is set, sent to <c>https://api.terrascale.tech</c>. Another region
    /// answers <see cref="TerraBaseWrongRegionException"/> instead of serving it. Unset, requests use the configured origin without a region pin.
    /// </summary>
    public string? Region { get; init; }

    /// <summary>
    /// A TerraBase API key (<c>tb_…</c>). Its organization selects the tenant; a key narrowed to a project or database
    /// reaches only that one. Leave it and <see cref="AccessTokenProvider"/> unset only for TerraBase's private listener,
    /// which needs no credential.
    /// </summary>
    public string? ApiKey { get; init; }

    /// <summary>
    /// Supplies the current bearer credential per request. Set this or ApiKey, exclusively.
    /// The provider must be thread-safe and honor cancellation; acquisition shares the request deadline.
    /// </summary>
    public Func<CancellationToken, ValueTask<string>>? AccessTokenProvider { get; init; }

    /// <inheritdoc />
    public override string ToString() => $"TerraBase client options for {BaseAddress}";
}

/// <summary>
/// Client of TerraBase project and database routes (<c>/v1/projects/{project}/…</c>), built over a caller-owned
/// <see cref="HttpClient"/> that it never disposes. Thread-safe. Requests are sent once: a lost response leaves the
/// outcome unknown. Do not automatically replay a mutation. Use <see cref="TerraBaseSqlClient"/> for SQL.
/// </summary>
public sealed class TerraBaseDatabaseClient
{
    private readonly TerraBaseConnection connection;

    /// <summary>
    /// Creates a client and validates <paramref name="options"/>. The caller-owned HttpClient must disable automatic
    /// redirects; use <see cref="TerraBaseHttp.CreateHandler"/>. The SDK cannot inspect or change its handler settings.
    /// </summary>
    public TerraBaseDatabaseClient(HttpClient httpClient, TerraBaseDatabaseClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        connection = new TerraBaseConnection(httpClient, options.BaseAddress, options.EndpointResolver, options.ApiKey, nameof(options.ApiKey), TerraBaseConnection.RequestDeadline, TerraBaseConnection.DefaultMaxResponseBytes,
            options.AccessTokenProvider, anonymousAllowed: true, region: options.Region);
    }

    internal TerraBaseDatabaseClient(TerraBaseConnection connection) => this.connection = connection;

    /// <summary>Canonical lifecycle and usage with project-token authority; does not create identity records.</summary>
    public TerraBaseProjectClient Project(string projectId) =>
        new(connection, $"/v1/projects/{TerraBaseConnection.Segment(projectId, nameof(projectId))}");

    /// <summary>Addresses one database: <paramref name="databaseName"/> in project <paramref name="projectId"/> of the key's organization.</summary>
    public TerraBaseDatabase Database(string projectId, string databaseName) =>
        new(connection, $"/v1/projects/{TerraBaseConnection.Segment(projectId, nameof(projectId))}/databases/{TerraBaseConnection.Segment(databaseName, nameof(databaseName))}",
            $"/v1/projects/{TerraBaseConnection.Segment(projectId, nameof(projectId))}");
}

/// <summary>A database as its idempotent create returns it.</summary>
/// <param name="Id">Database id, a canonical uppercase ULID.</param>
/// <param name="ProjectId">Owning project.</param>
/// <param name="Name">Database name, unique within the project.</param>
public sealed record TerraBaseDatabaseInfo(string Id, string ProjectId, string Name)
{
    /// <summary>Every selected region, sorted by id without ordering meaning.</summary>
    public IReadOnlyList<string> Regions { get; init; } = [];

    /// <summary>Placement epoch; unchanged by an identical region set.</summary>
    public long PlacementVersion { get; init; }

    /// <summary>Whether every serving region holds this database.</summary>
    public bool AllRegions { get; init; }
}

/// <summary>One TerraBase database. Thread-safe; obtain it from <see cref="TerraBaseDatabaseClient.Database"/>.</summary>
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

    /// <summary>
    /// Idempotent database creation (<c>PUT</c>) for a server-accepted API key.
    /// Project access tokens are refused by this producer route; use
    /// <see cref="TerraBaseProjectClient.CreateDatabaseAsync"/> for canonical project-token creation (<c>POST</c>).
    /// </summary>
    public async Task<TerraBaseDatabaseInfo> CreateAsync(CancellationToken cancellationToken = default) =>
        (await connection.SendAsync(HttpMethod.Put, path, null, Json.TerraBaseDatabaseInfo, null, cancellationToken).ConfigureAwait(false)).Value;

    /// <summary>
    /// Deletes the database and its data in its region (<c>DELETE</c>). Branches are independent and stay.
    /// A 204 response records logical deletion and local cleanup; remote erasure is retried durably, not guaranteed synchronous.
    /// </summary>
    public Task DeleteAsync(CancellationToken cancellationToken = default) =>
        connection.SendAsync(HttpMethod.Delete, path, null, null, cancellationToken);

    /// <summary>Reads best-effort dashboard usage, not billing. Granularity is hour or day; from/to are RFC 3339 instants.</summary>
    public async Task<TerraBaseDatabaseUsage> GetUsageAsync(string? granularity = null, DateTimeOffset? from = null, DateTimeOffset? to = null, CancellationToken cancellationToken = default) =>
        (await connection.SendAsync(HttpMethod.Get, path + "/usage" + TerraBaseProjectClient.UsageQuery(granularity, from, to),
            null, Json.TerraBaseDatabaseUsage, null, cancellationToken).ConfigureAwait(false)).Value;

    /// <summary>getDatabaseRegions: reads selected regions and writer-observed pull progress; not application acknowledgements.</summary>
    public async Task<TerraBaseDatabaseRegions> GetRegionsAsync(CancellationToken cancellationToken = default) =>
        (await connection.SendAsync(HttpMethod.Get, path + "/regions", null, Json.TerraBaseDatabaseRegions, null, cancellationToken).ConfigureAwait(false)).Value;

    /// <summary>Branches a consistent snapshot in the source database's region. Requires databases:write; sent once.</summary>
    public async Task<TerraBaseManagedDatabase> BranchAsync(string name, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        return (await connection.SendAsync(HttpMethod.Post, path + "/branches", TerraBaseConnection.Json(new RequestBody { Name = name }, Json.RequestBody),
            Json.TerraBaseManagedDatabase, null, cancellationToken).ConfigureAwait(false)).Value;
    }

    /// <summary>
    /// Branches a consistent snapshot only while the source's immutable ID is still <paramref name="expectedId"/>; a changed
    /// identity answers 409 before any copy work. Older servers without the identity-conditioned route answer 404.
    /// </summary>
    public async Task<TerraBaseManagedDatabase> BranchIfIdAsync(string name, string expectedId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        TerraScaleIds.Validate(expectedId);
        return (await connection.SendAsync(HttpMethod.Post, path + "/branches/if-id/" + Uri.EscapeDataString(expectedId),
            TerraBaseConnection.Json(new RequestBody { Name = name }, Json.RequestBody),
            Json.TerraBaseManagedDatabase, null, cancellationToken).ConfigureAwait(false)).Value;
    }

    /// <summary>
    /// Deletes the database only while its immutable ID is still <paramref name="expectedId"/>; a changed identity answers 409
    /// before any deletion work. Older servers without the identity-conditioned route answer 404.
    /// </summary>
    public Task DeleteIfIdAsync(string expectedId, CancellationToken cancellationToken = default)
    {
        TerraScaleIds.Validate(expectedId);
        return connection.SendAsync(HttpMethod.Delete, path + "/if-id/" + Uri.EscapeDataString(expectedId), null, null, cancellationToken);
    }
}

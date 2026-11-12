using System.Collections.Concurrent;
using System.Text.Json.Serialization.Metadata;

namespace TerraScale.DotNet.TerraBase;

/// <summary>Configures a <see cref="TerraBaseSqlClient"/>.</summary>
public sealed record TerraBaseSqlClientOptions
{
    /// <summary>
    /// HTTPS origin of the TerraBase client listener, for example <c>https://api.terrascale.tech</c>, or the plain-HTTP
    /// origin of a private listener on loopback (localhost, 127.0.0.0/8 or [::1]). With an ApiKey and no
    /// EndpointResolver or AuthAddress it defaults to <c>https://api.terrascale.tech</c>.
    /// </summary>
    public Uri? BaseAddress { get; init; }

    /// <summary>Resolves the client origin per request. Set this or BaseAddress, exclusively.</summary>
    public ITerraBaseEndpointResolver? EndpointResolver { get; init; }

    /// <summary>
    /// A region id such as <c>gru</c>: every request is pinned to that region and, unless BaseAddress or
    /// EndpointResolver is set, sent to <c>https://api.terrascale.tech</c>.
    /// </summary>
    public string? Region { get; init; }

    /// <summary>
    /// A TerraScale API key from the dashboard with <c>sql:read</c>; writes and schema changes also need
    /// <c>sql:write</c>. Database lifecycle mutations need <c>databases:write</c>. The client exchanges the key
    /// at ts-auth (<see cref="AuthAddress"/>) for an access token to each operation's project, caches the token and
    /// renews it before it expires. The key is sent only to that ts-auth origin.
    /// </summary>
    public string? ApiKey { get; init; }

    /// <summary>
    /// Origin of the ts-auth that exchanges <see cref="ApiKey"/>, for example <c>https://auth.terrascale.tech</c>, or the
    /// plain-HTTP origin of a private ts-auth on loopback. It defaults to
    /// <c>https://auth.terrascale.tech</c> only when requests go to TerraScale's public origin (no BaseAddress or
    /// EndpointResolver, or BaseAddress <c>https://api.terrascale.tech</c>); with any other TerraBase origin an ApiKey
    /// needs it, so the key never leaves for a host the caller did not name.
    /// </summary>
    public Uri? AuthAddress { get; init; }

    /// <summary>Supplies the current bearer credential per request or connection. Set this or ApiKey, exclusively.</summary>
    public Func<CancellationToken, ValueTask<string>>? AccessTokenProvider { get; init; }

    /// <inheritdoc />
    public override string ToString() => $"TerraBase SQL client options for {BaseAddress?.ToString() ?? Region}";
}

/// <summary>
/// The TerraBase SQL client: SQL over REST (<c>…/sql/execute</c>) and over the WebSocket (<c>…/sql/ws</c>). Built over
/// a caller-owned <see cref="HttpClient"/> that it never disposes. Thread-safe.
/// </summary>
public sealed partial class TerraBaseSqlClient
{
    private readonly Func<Func<CancellationToken, ValueTask<string>>?, TerraBaseConnection>? connect;
    private readonly TerraScaleApiKeyTokens? apiKeyTokens;
    private readonly ConcurrentDictionary<string, TerraBaseConnection>? projectConnections;
    private readonly TerraBaseConnection? connection;

    /// <summary>
    /// Creates a client and validates <paramref name="options"/>. The HttpClient must disable automatic redirects; use
    /// <see cref="TerraBaseHttp.CreateHandler"/>.
    /// </summary>
    public TerraBaseSqlClient(HttpClient httpClient, TerraBaseSqlClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.ApiKey is null)
        {
            if (options.AuthAddress is not null) throw new ArgumentException("AuthAddress exchanges an ApiKey; set ApiKey or remove AuthAddress.", nameof(options));
            connection = new TerraBaseConnection(httpClient, options.BaseAddress, options.EndpointResolver, null, nameof(options.ApiKey), TerraBaseConnection.RequestDeadline,
                TerraBaseConnection.DefaultMaxResponseBytes, options.AccessTokenProvider, anonymousAllowed: true, region: options.Region);
            return;
        }

        if (options.AccessTokenProvider is not null)
        {
            throw new ArgumentException("Set either ApiKey or AccessTokenProvider, not both.", nameof(options));
        }

        ArgumentNullException.ThrowIfNull(httpClient);
        var baseAddress = options.AuthAddress is null
            ? TerraBaseRegion.OriginOrPublic(options.BaseAddress, options.EndpointResolver)
            : options.BaseAddress;
        connect = provider => new TerraBaseConnection(httpClient, baseAddress, options.EndpointResolver, null, nameof(options.ApiKey), TerraBaseConnection.RequestDeadline,
            TerraBaseConnection.DefaultMaxResponseBytes, provider, region: options.Region);
        _ = connect(static _ => ValueTask.FromResult(string.Empty)); // validates the endpoint options now
        apiKeyTokens = new TerraScaleApiKeyTokens(httpClient, AuthOrigin(options), options.ApiKey, nameof(options.ApiKey), TimeProvider.System);
        projectConnections = new(StringComparer.Ordinal);
    }

    internal TerraBaseSqlClient(TerraBaseConnection connection) => this.connection = connection;

    private static Uri AuthOrigin(TerraBaseSqlClientOptions options) => options switch
    {
        { AuthAddress: { } auth } => TerraScaleOrigin.Parse(auth, nameof(options.AuthAddress), nameof(options), TerraScaleService.Auth),
        { EndpointResolver: null, BaseAddress: null } => TerraScaleApiKeyTokens.ProductionOrigin,
        { EndpointResolver: null, BaseAddress: { } address } when address == TerraBaseRegion.PublicOrigin => TerraScaleApiKeyTokens.ProductionOrigin,
        _ => throw new ArgumentException(
            "Set AuthAddress to the ts-auth that exchanges the ApiKey: with a TerraBase origin other than https://api.terrascale.tech the key is not sent to production ts-auth.",
            nameof(options)),
    };

    /// <summary>
    /// Creates a client from TERRASCALE_API_KEY (required) and the optional TERRASCALE_BASE_URL and TERRASCALE_REGION,
    /// exchanging the dashboard key at ts-auth for cached, auto-refreshed project access tokens. The client owns its
    /// HttpClient (redirects disabled, <see cref="TerraBaseHttp.CreateHandler"/>) for its whole lifetime. Operations
    /// name their project: <c>sql.Database(projectId, databaseName)</c>.
    /// </summary>
    public static TerraBaseSqlClient FromEnvironment()
    {
        var apiKey = Environment.GetEnvironmentVariable("TERRASCALE_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new ArgumentException("TERRASCALE_API_KEY is not set; export the dashboard API key or pass TerraBaseSqlClientOptions.ApiKey.");
        var options = new TerraBaseSqlClientOptions { ApiKey = apiKey };
        var baseUrl = Environment.GetEnvironmentVariable("TERRASCALE_BASE_URL");
        if (!string.IsNullOrWhiteSpace(baseUrl))
            options = options with { BaseAddress = TerraScaleOrigin.Address(baseUrl, "TERRASCALE_BASE_URL", TerraScaleService.Api) };
        var region = Environment.GetEnvironmentVariable("TERRASCALE_REGION");
        if (!string.IsNullOrWhiteSpace(region))
            options = options with { Region = region };
        return new TerraBaseSqlClient(new HttpClient(TerraBaseHttp.CreateHandler()), options);
    }

    /// <summary>
    /// Addresses database <paramref name="database"/> (its name or ULID) in project <paramref name="projectId"/>. With
    /// an ApiKey, the project's access token is requested on the first call.
    /// </summary>
    public TerraBaseSqlDatabase Database(string projectId, string database)
    {
        var path = $"/v1/projects/{TerraBaseConnection.Segment(projectId, nameof(projectId))}/databases/{TerraBaseConnection.Segment(database, nameof(database))}";
        return new(ProjectConnection(projectId), path);
    }
}

/// <summary>
/// One database over SQL REST. Every call is one request and one transaction, all or nothing. A request refused with
/// <see cref="TerraBaseSqlErrorCodes.SerializationConflict"/> rolled back and is resent at most twice; every other
/// refusal throws <see cref="TerraBaseSqlException"/> at once. Thread-safe.
/// </summary>
public sealed class TerraBaseSqlDatabase
{
    private readonly TerraBaseConnection connection;
    private readonly string path;

    internal TerraBaseSqlDatabase(TerraBaseConnection connection, string path)
    {
        this.connection = connection;
        this.path = path;
    }

    /// <summary>Reads selected regions and writer-observed pull progress; a pull is not an application acknowledgement.</summary>
    public async Task<TerraBaseDatabaseRegions> GetRegionsAsync(CancellationToken cancellationToken = default) =>
        (await connection.SendAsync(HttpMethod.Get, path + "/regions", null,
            TerraBaseJsonContext.Wire.TerraBaseDatabaseRegions, null, cancellationToken).ConfigureAwait(false)).Value;

    /// <summary>Runs one statement with positional arguments.</summary>
    public Task<TerraBaseSqlStatementResult> ExecuteAsync(string sql, IReadOnlyList<TerraBaseSqlValue>? args = null, CancellationToken cancellationToken = default) =>
        ExecuteAsync(new TerraBaseSqlStatement(sql, args), cancellationToken);

    /// <summary>Runs one statement with named arguments.</summary>
    public Task<TerraBaseSqlStatementResult> ExecuteAsync(string sql, IReadOnlyDictionary<string, TerraBaseSqlValue> named, CancellationToken cancellationToken = default) =>
        ExecuteAsync(new TerraBaseSqlStatement(sql, Named: named), cancellationToken);

    /// <summary>Runs one statement.</summary>
    public async Task<TerraBaseSqlStatementResult> ExecuteAsync(TerraBaseSqlStatement statement, CancellationToken cancellationToken = default) =>
        (await BatchAsync([statement], cancellationToken).ConfigureAwait(false))[0];

    /// <summary>Runs one statement and returns its rows.</summary>
    public async Task<IReadOnlyList<TerraBaseSqlRecord>> QueryAsync(string sql, IReadOnlyList<TerraBaseSqlValue>? args = null, CancellationToken cancellationToken = default) =>
        (await ExecuteAsync(sql, args, cancellationToken).ConfigureAwait(false)).Rows;

    /// <summary>Runs one statement with named arguments and returns its rows.</summary>
    public async Task<IReadOnlyList<TerraBaseSqlRecord>> QueryAsync(string sql, IReadOnlyDictionary<string, TerraBaseSqlValue> named, CancellationToken cancellationToken = default) =>
        (await ExecuteAsync(sql, named, cancellationToken).ConfigureAwait(false)).Rows;

    /// <summary>Runs one statement and maps its rows to <typeparamref name="T"/>; see <see cref="TerraBaseSqlRecord.As{T}"/>.</summary>
    public async Task<IReadOnlyList<T>> QueryAsync<T>(string sql, IReadOnlyList<TerraBaseSqlValue>? args, JsonTypeInfo<T> typeInfo, CancellationToken cancellationToken = default) =>
        (await ExecuteAsync(sql, args, cancellationToken).ConfigureAwait(false)).As(typeInfo);

    /// <summary>Runs one statement with named arguments and maps its rows to <typeparamref name="T"/>.</summary>
    public async Task<IReadOnlyList<T>> QueryAsync<T>(string sql, IReadOnlyDictionary<string, TerraBaseSqlValue> named, JsonTypeInfo<T> typeInfo, CancellationToken cancellationToken = default) =>
        (await ExecuteAsync(sql, named, cancellationToken).ConfigureAwait(false)).As(typeInfo);

    /// <summary>
    /// Runs 1 to 256 statements as one transaction, all or nothing, in a body of at most 1 MiB, and returns one result
    /// per statement in order. A refused statement rolls everything back; <see cref="TerraBaseSqlException.Statement"/>
    /// names it.
    /// </summary>
    public async Task<IReadOnlyList<TerraBaseSqlStatementResult>> BatchAsync(IReadOnlyList<TerraBaseSqlStatement> statements, CancellationToken cancellationToken = default)
    {
        TerraBaseSqlStatement.Validate(statements);
        var json = TerraBaseSqlJsonContext.Wire;
        var (response, status) = await connection.SendAsync(HttpMethod.Post, path + "/sql/execute",
            TerraBaseConnection.Json(new TerraBaseSqlExecuteRequest(statements), json.TerraBaseSqlExecuteRequest), json.TerraBaseSqlExecuteResponse, null, cancellationToken).ConfigureAwait(false);
        return response.ToResults(statements.Count, status);
    }

    /// <summary>
    /// Streams the database as one SQL dump (BEGIN, every user table's CREATE TABLE and rows, then indexes, views and
    /// triggers, then COMMIT) from one read snapshot; the same SQL dialect runs it back into a fresh database. Needs
    /// sql:read; the region that orders the database's writes serves the export. The caller owns and disposes the stream.
    /// </summary>
    public Task<Stream> ExportDatabaseAsync(CancellationToken cancellationToken = default) =>
        connection.OpenStreamAsync(path + "/export", "text/plain", cancellationToken);

    /// <summary>
    /// Opens a SQL WebSocket session for interactive transactions. The caller owns and disposes it. The upgrade goes
    /// through the client's HttpClient and its handlers, with the same credential as REST; a client without a credential
    /// opens it over HTTP/2 (extended CONNECT) like its REST requests.
    /// </summary>
    public Task<TerraBaseSqlSession> ConnectAsync(CancellationToken cancellationToken = default) =>
        TerraBaseSqlSession.ConnectAsync(connection, path + "/sql/ws", cancellationToken);
}

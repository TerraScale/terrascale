using System.Collections.Concurrent;
using System.Text.Json.Serialization.Metadata;

namespace TerraScale.DotNet.TerraBase;

public sealed record TerraBaseSqlClientOptions
{
    public Uri? BaseAddress { get; init; }

    public ITerraBaseEndpointResolver? EndpointResolver { get; init; }

    public string? Region { get; init; }

    public string? ApiKey { get; init; }

    public Uri? AuthAddress { get; init; }

    public Func<CancellationToken, ValueTask<string>>? AccessTokenProvider { get; init; }

    public TerraBaseConsistencyMode? Consistency { get; init; }

    public override string ToString() => $"TerraBase SQL client options for {BaseAddress?.ToString() ?? Region}";
}

public sealed partial class TerraBaseSqlClient
{
    private readonly ProjectApiKey? projectApiKey;
    private readonly TerraBaseConnection? connection;
    private readonly ConcurrentDictionary<string, TerraBaseBookmarkTracker> bookmarks = new(StringComparer.Ordinal);
    private readonly TerraBaseConsistencyMode? consistency;

    public TerraBaseSqlClient(HttpClient httpClient, TerraBaseSqlClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        consistency = options.Consistency;
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
        TerraBaseConnection Connect(Func<CancellationToken, ValueTask<string>> provider, Action<string>? reject) => new(httpClient, baseAddress, options.EndpointResolver, null, nameof(options.ApiKey), TerraBaseConnection.RequestDeadline,
            TerraBaseConnection.DefaultMaxResponseBytes, provider, region: options.Region, rejectAccessToken: reject);
        _ = Connect(static _ => throw new InvalidOperationException("The connection has no project credential."), null);
        projectApiKey = new(new TerraScaleApiKeyTokens(httpClient, AuthOrigin(options), options.ApiKey, nameof(options.ApiKey), TimeProvider.System), Connect);
    }

    internal TerraBaseSqlClient(TerraBaseConnection sharedConnection) => connection = sharedConnection;

    private sealed class ProjectApiKey(TerraScaleApiKeyTokens tokens, Func<Func<CancellationToken, ValueTask<string>>, Action<string>?, TerraBaseConnection> connect)
    {
        private readonly ConcurrentDictionary<string, TerraBaseConnection> connections = new(StringComparer.Ordinal);

        internal TerraScaleApiKeyTokens Tokens { get; } = tokens;

        internal TerraBaseConnection For(string project) => connections.GetOrAdd(project, id =>
        {
            var token = Tokens.For(id);
            return connect(token.GetAsync, token.Reject);
        });
    }

    private static Uri AuthOrigin(TerraBaseSqlClientOptions options) => options switch
    {
        { AuthAddress: { } auth } => TerraScaleOrigin.Parse(auth, nameof(options.AuthAddress), nameof(options), TerraScaleService.Auth),
        { EndpointResolver: null, BaseAddress: null } => TerraScaleApiKeyTokens.ProductionOrigin,
        { EndpointResolver: null, BaseAddress: { } address } when address == TerraBaseRegion.PublicOrigin => TerraScaleApiKeyTokens.ProductionOrigin,
        _ => throw new ArgumentException(
            "Set AuthAddress to the ts-auth that exchanges the ApiKey: with a TerraBase origin other than https://api.terrascale.tech the key is not sent to production ts-auth.",
            nameof(options)),
    };

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

    public TerraBaseSqlDatabase Database(string projectId, string database)
    {
        var path = $"/v1/projects/{TerraScalePaths.Segment(projectId, nameof(projectId))}/databases/{TerraScalePaths.Segment(database, nameof(database))}";
        return new(ProjectConnection(projectId), path, bookmarks.GetOrAdd(path, static _ => new TerraBaseBookmarkTracker()), consistency);
    }
}

public sealed class TerraBaseSqlDatabase
{
    private static readonly TerraBaseSqlRequestOptions NoOptions = new();

    private readonly TerraBaseConnection connection;
    private readonly string path;
    private readonly TerraBaseBookmarkTracker bookmarks;

    internal TerraBaseSqlDatabase(TerraBaseConnection connection, string path, TerraBaseBookmarkTracker bookmarks, TerraBaseConsistencyMode? consistency)
    {
        this.connection = connection;
        this.path = path;
        this.bookmarks = bookmarks;
        Consistency = consistency;
    }

    public TerraBaseConsistencyMode? Consistency { get; }

    public string Bookmark => bookmarks.Current;

    public void ObserveBookmark(string bookmark) => bookmarks.Observe(bookmark, nameof(bookmark));

    public TerraBaseSqlDatabase WithConsistency(TerraBaseConsistencyMode? consistency) => new(connection, path, bookmarks, consistency);

    public LiveShape Shape(string table, ShapeOptions? options = null) => new(connection, path, table, options ?? new());

    public async Task<TerraBaseDatabaseRegions> GetRegionsAsync(CancellationToken cancellationToken = default) =>
        (await connection.SendAsync(HttpMethod.Get, path + "/regions", null,
            TerraBaseJsonContext.Wire.TerraBaseDatabaseRegions, null, cancellationToken).ConfigureAwait(false)).Value;

    public Task<TerraBaseSqlStatementResult> ExecuteAsync(string sql, IReadOnlyList<TerraBaseSqlValue>? args = null, CancellationToken cancellationToken = default) =>
        ExecuteAsync(new TerraBaseSqlStatement(sql, args), cancellationToken);

    public Task<TerraBaseSqlStatementResult> ExecuteAsync(string sql, IReadOnlyDictionary<string, TerraBaseSqlValue> named, CancellationToken cancellationToken = default) =>
        ExecuteAsync(new TerraBaseSqlStatement(sql, Named: named), cancellationToken);

    public Task<TerraBaseSqlStatementResult> ExecuteAsync(string sql, IReadOnlyList<TerraBaseSqlValue>? args, TerraBaseSqlRequestOptions options,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(new TerraBaseSqlStatement(sql, args), options, cancellationToken);

    public async Task<TerraBaseSqlStatementResult> ExecuteAsync(TerraBaseSqlStatement statement, CancellationToken cancellationToken = default) =>
        (await BatchAsync([statement], cancellationToken).ConfigureAwait(false))[0];

    public async Task<TerraBaseSqlStatementResult> ExecuteAsync(TerraBaseSqlStatement statement, TerraBaseSqlRequestOptions options, CancellationToken cancellationToken = default) =>
        (await BatchAsync([statement], options, cancellationToken).ConfigureAwait(false))[0];

    public async Task<IReadOnlyList<TerraBaseSqlRecord>> QueryAsync(string sql, IReadOnlyList<TerraBaseSqlValue>? args = null, CancellationToken cancellationToken = default) =>
        (await ExecuteAsync(sql, args, cancellationToken).ConfigureAwait(false)).Rows;

    public async Task<IReadOnlyList<TerraBaseSqlRecord>> QueryAsync(string sql, IReadOnlyList<TerraBaseSqlValue>? args, TerraBaseSqlRequestOptions options,
        CancellationToken cancellationToken = default) =>
        (await ExecuteAsync(sql, args, options, cancellationToken).ConfigureAwait(false)).Rows;

    public async Task<IReadOnlyList<TerraBaseSqlRecord>> QueryAsync(string sql, IReadOnlyDictionary<string, TerraBaseSqlValue> named, CancellationToken cancellationToken = default) =>
        (await ExecuteAsync(sql, named, cancellationToken).ConfigureAwait(false)).Rows;

    public async Task<IReadOnlyList<T>> QueryAsync<T>(string sql, IReadOnlyList<TerraBaseSqlValue>? args, JsonTypeInfo<T> typeInfo, CancellationToken cancellationToken = default) =>
        (await ExecuteAsync(sql, args, cancellationToken).ConfigureAwait(false)).As(typeInfo);

    public async Task<IReadOnlyList<T>> QueryAsync<T>(string sql, IReadOnlyDictionary<string, TerraBaseSqlValue> named, JsonTypeInfo<T> typeInfo, CancellationToken cancellationToken = default) =>
        (await ExecuteAsync(sql, named, cancellationToken).ConfigureAwait(false)).As(typeInfo);

    public Task<IReadOnlyList<TerraBaseSqlStatementResult>> BatchAsync(IReadOnlyList<TerraBaseSqlStatement> statements, CancellationToken cancellationToken = default) =>
        BatchAsync(statements, NoOptions, cancellationToken);

    public Task<IReadOnlyList<TerraBaseSqlStatementResult>> BatchAsync(IReadOnlyList<TerraBaseSqlStatement> statements, TerraBaseSqlRequestOptions options,
        CancellationToken cancellationToken = default) =>
        BatchAsync(statements, options, null, cancellationToken);

    internal async Task<IReadOnlyList<TerraBaseSqlStatementResult>> BatchAsync(IReadOnlyList<TerraBaseSqlStatement> statements, TerraBaseSqlRequestOptions options,
        TimeSpan? requestTimeout, CancellationToken cancellationToken)
    {
        TerraBaseSqlStatement.Validate(statements);
        ArgumentNullException.ThrowIfNull(options);
        var bookmark = bookmarks.Outgoing(options.Bookmark);
        var mode = TerraBaseConsistency.Format(options.Consistency ?? Consistency);
        var json = TerraBaseSqlJsonContext.Wire;
        var (response, status) = await connection.SendAsync(HttpMethod.Post, path + "/sql/execute",
            TerraBaseConnection.Json(new TerraBaseSqlExecuteRequest(statements), json.TerraBaseSqlExecuteRequest), json.TerraBaseSqlExecuteResponse,
            headers =>
            {
                if (bookmark.Length > 0) headers.Add(TerraBaseConsistency.BookmarkHeader, bookmark);
                if (mode is not null) headers.Add(TerraBaseConsistency.ConsistencyHeader, mode);
            },
            cancellationToken, requestTimeout, bookmarks.ObserveResponse).ConfigureAwait(false);
        bookmarks.ObserveAnswer(response.Bookmark, status);
        return TerraBaseSqlScript.Frame(statements).Results(response.Results, statements.Count, status);
    }

    public Task<Stream> ExportDatabaseAsync(CancellationToken cancellationToken = default) =>
        connection.OpenStreamAsync(path + "/export", "text/plain", cancellationToken);

    public Task<TerraBaseSqlSession> ConnectAsync(CancellationToken cancellationToken = default) =>
        TerraBaseSqlSession.ConnectAsync(connection, path + "/sql/ws", bookmarks, Consistency, cancellationToken);
}

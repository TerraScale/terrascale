using System.Text.Json.Serialization.Metadata;

namespace TerraScale.DotNet.TerraBase;

/// <summary>Configures a <see cref="TerraBaseSqlClient"/>.</summary>
public sealed record TerraBaseSqlClientOptions
{
    /// <summary>
    /// HTTPS origin of the TerraBase client listener, for example <c>https://api.terrascale.tech</c>, or the plain-HTTP
    /// origin of a private listener on NetBird <c>100.64.0.0/10</c> addresses or loopback.
    /// </summary>
    public Uri? BaseAddress { get; init; }

    /// <summary>Resolves the client origin per request. Set this or BaseAddress, exclusively.</summary>
    public ITerraBaseEndpointResolver? EndpointResolver { get; init; }

    /// <summary>
    /// A region id such as <c>gru</c>: every request is pinned to that region and, unless BaseAddress or
    /// EndpointResolver is set, sent to <c>https://api.terrascale.tech</c>.
    /// </summary>
    public string? Region { get; init; }

    /// <summary>An API key with <c>documents:read</c>; writes and schema changes also need <c>documents:write</c>.</summary>
    public string? ApiKey { get; init; }

    /// <summary>Supplies the current bearer credential per request. Set this or ApiKey, exclusively.</summary>
    public Func<CancellationToken, ValueTask<string>>? AccessTokenProvider { get; init; }

    /// <inheritdoc />
    public override string ToString() => $"TerraBase SQL client options for {BaseAddress?.ToString() ?? Region}";
}

/// <summary>
/// The TerraBase SQL client: SQL over REST (<c>…/sql/execute</c>). Built over
/// a caller-owned <see cref="HttpClient"/> that it never disposes. Thread-safe.
/// </summary>
public sealed class TerraBaseSqlClient
{
    private readonly TerraBaseConnection connection;

    /// <summary>
    /// Creates a client and validates <paramref name="options"/>. The HttpClient must disable automatic redirects; use
    /// <see cref="TerraBaseHttp.CreateHandler"/>.
    /// </summary>
    public TerraBaseSqlClient(HttpClient httpClient, TerraBaseSqlClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        connection = new TerraBaseConnection(httpClient, options.BaseAddress, options.EndpointResolver, options.ApiKey, nameof(options.ApiKey), TerraBaseConnection.RequestDeadline,
            TerraBaseConnection.DefaultMaxResponseBytes, options.AccessTokenProvider, anonymousAllowed: true, region: options.Region);
    }

    internal TerraBaseSqlClient(TerraBaseConnection connection) => this.connection = connection;

    /// <summary>Addresses database <paramref name="database"/> (its name or ULID) in project <paramref name="projectId"/>.</summary>
    public TerraBaseSqlDatabase Database(string projectId, string database) =>
        new(connection, $"/v1/projects/{TerraBaseConnection.Segment(projectId, nameof(projectId))}/databases/{TerraBaseConnection.Segment(database, nameof(database))}");
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

}

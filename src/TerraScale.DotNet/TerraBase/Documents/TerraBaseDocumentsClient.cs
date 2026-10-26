using System.Net;
using System.Text.Json;

namespace TerraScale.DotNet.TerraBase;

/// <summary>Configures a <see cref="TerraBaseDocumentsClient"/>.</summary>
public sealed record TerraBaseDocumentsClientOptions
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
    /// A TerraBase API key (<c>tb_…</c>) with <c>documents:read</c> and/or <c>documents:write</c>. Its organization
    /// selects the tenant; a key narrowed to a project or database reaches only that one. Leave it and
    /// <see cref="AccessTokenProvider"/> unset only for TerraBase's private listener, which needs no credential.
    /// </summary>
    public string? ApiKey { get; init; }

    /// <summary>
    /// Supplies the current bearer credential per request. Set this or ApiKey, exclusively.
    /// The provider must be thread-safe and honor cancellation; acquisition shares the request deadline.
    /// </summary>
    public Func<CancellationToken, ValueTask<string>>? AccessTokenProvider { get; init; }

    /// <inheritdoc />
    public override string ToString() => $"TerraBase documents client options for {BaseAddress}";
}

/// <summary>
/// Client of the TerraBase Documents API v1 (<c>/v1/projects/{project}/databases/{database}/…</c>), built over a
/// caller-owned <see cref="HttpClient"/> that it never disposes. Thread-safe. Requests are sent once: TerraBase does not
/// deduplicate retried writes yet, so a lost response leaves the outcome unknown. Do not automatically replay a mutation.
/// </summary>
public sealed class TerraBaseDocumentsClient
{
    private readonly TerraBaseConnection connection;

    /// <summary>
    /// Creates a client and validates <paramref name="options"/>. The caller-owned HttpClient must disable automatic
    /// redirects; use <see cref="TerraBaseHttp.CreateHandler"/>. The SDK cannot inspect or change its handler settings.
    /// </summary>
    public TerraBaseDocumentsClient(HttpClient httpClient, TerraBaseDocumentsClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        connection = new TerraBaseConnection(httpClient, options.BaseAddress, options.EndpointResolver, options.ApiKey, nameof(options.ApiKey), TerraBaseConnection.RequestDeadline, TerraBaseConnection.DefaultMaxResponseBytes,
            options.AccessTokenProvider, anonymousAllowed: true, region: options.Region);
    }

    internal TerraBaseDocumentsClient(TerraBaseConnection connection) => this.connection = connection;

    /// <summary>Canonical lifecycle and usage with project-token authority; does not create identity records.</summary>
    public TerraBaseProjectClient Project(string projectId) =>
        new(connection, $"/v1/projects/{TerraBaseConnection.Segment(projectId, nameof(projectId))}");

    /// <summary>Addresses one database: <paramref name="databaseName"/> in project <paramref name="projectId"/> of the key's organization.</summary>
    public TerraBaseDatabase Database(string projectId, string databaseName) =>
        new(connection, $"/v1/projects/{TerraBaseConnection.Segment(projectId, nameof(projectId))}/databases/{TerraBaseConnection.Segment(databaseName, nameof(databaseName))}");
}

/// <summary>One TerraBase database of the Documents API. Thread-safe; obtain it from <see cref="TerraBaseDocumentsClient.Database"/>.</summary>
public sealed partial class TerraBaseDatabase
{
    private const string MergePatchMediaType = "application/merge-patch+json";
    private const string OpsPatchMediaType = "application/vnd.terrabase.ops+json";
    private static readonly TerraBaseJsonContext Json = TerraBaseJsonContext.Wire;

    private readonly TerraBaseConnection connection;
    private readonly string path;

    internal TerraBaseDatabase(TerraBaseConnection connection, string path)
    {
        this.connection = connection;
        this.path = path;
    }

    /// <summary>
    /// Legacy idempotent database creation (<c>PUT</c>) for a server-accepted legacy API key.
    /// Project access tokens are refused by this producer route; use
    /// <see cref="TerraBaseProjectClient.CreateDatabaseAsync"/> for canonical project-token creation (<c>POST</c>).
    /// </summary>
    public async Task<TerraBaseDatabaseInfo> CreateAsync(CancellationToken cancellationToken = default) =>
        (await connection.SendAsync(HttpMethod.Put, path, null, Json.TerraBaseDatabaseInfo, null, cancellationToken).ConfigureAwait(false)).Value;

    /// <summary>
    /// Deletes the database and its data directly in its home region (<c>DELETE</c>), without a preceding placement update.
    /// Permanent purge authority removes every replica, including all-regions placement. Branches are independent and stay.
    /// A 204 response records logical deletion and local cleanup; remote erasure is retried durably, not guaranteed synchronous.
    /// </summary>
    public Task DeleteAsync(CancellationToken cancellationToken = default) =>
        connection.SendAsync(HttpMethod.Delete, path, null, null, cancellationToken);

    /// <summary>Reads best-effort dashboard usage, not billing. Granularity is hour or day; from/to are RFC 3339 instants.</summary>
    public async Task<TerraBaseDatabaseUsage> GetUsageAsync(string? granularity = null, DateTimeOffset? from = null, DateTimeOffset? to = null, CancellationToken cancellationToken = default) =>
        (await connection.SendAsync(HttpMethod.Get, path + "/usage" + TerraBaseProjectClient.UsageQuery(granularity, from, to),
            null, Json.TerraBaseDatabaseUsage, null, cancellationToken).ConfigureAwait(false)).Value;

    /// <summary>Lists collections in name order.</summary>
    /// <param name="limit">1 to 200; TerraBase's default is 50.</param>
    /// <param name="cursor">The previous page's <see cref="TerraBaseCollectionPage.NextCursor"/>.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    public async Task<TerraBaseCollectionPage> ListCollectionsAsync(int? limit = null, string? cursor = null, CancellationToken cancellationToken = default) =>
        (await connection.SendAsync(
            HttpMethod.Get, path + "/collections" + TerraBaseConnection.Query(("limit", TerraBaseConnection.Number(limit)), ("cursor", cursor)),
            null, Json.TerraBaseCollectionPage, null, cancellationToken).ConfigureAwait(false)).Value;

    /// <summary>Creates a collection; an existing name throws <see cref="TerraBaseConflictException"/> (<c>name_taken</c>).</summary>
    public async Task<TerraBaseCollection> CreateCollectionAsync(string name, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        var body = TerraBaseConnection.Json(new RequestBody { Name = name }, Json.RequestBody);
        return (await connection.SendAsync(HttpMethod.Post, path + "/collections", body, Json.TerraBaseCollection, null, cancellationToken).ConfigureAwait(false)).Value;
    }

    /// <summary>Reads a collection.</summary>
    public async Task<TerraBaseCollection> GetCollectionAsync(string name, CancellationToken cancellationToken = default) =>
        (await connection.SendAsync(HttpMethod.Get, CollectionPath(name), null, Json.TerraBaseCollection, null, cancellationToken).ConfigureAwait(false)).Value;

    /// <summary>Drops a collection and every document in it, in one transaction; returns the dropped collection.</summary>
    public async Task<TerraBaseCollection> DropCollectionAsync(string name, CancellationToken cancellationToken = default) =>
        (await connection.SendAsync(HttpMethod.Delete, CollectionPath(name), null, Json.TerraBaseCollection, null, cancellationToken).ConfigureAwait(false)).Value;

    /// <summary>
    /// Declares single and compound unique keys (<c>PUT …/schema/{collection}</c>). Requires a ts-auth project token
    /// with <c>databases:write</c> in the current producer contract. Declarations may only change when the collection
    /// has no retained document states (including tombstones); identical declarations are idempotent.
    /// Otherwise, <see cref="TerraBaseConflictException"/> is thrown
    /// with <c>schema_requires_empty_collection</c>. Local duplicate writes throw <c>unique_violation</c> and roll back
    /// the whole transaction. Replicas can admit competing claims during partitions; this is not globally exclusive admission.
    /// </summary>
    public async Task<TerraBaseUniqueSchema> DeclareSchemaAsync(
        string collection, TerraBaseUniqueSchemaRequest schema, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(schema);
        var body = TerraBaseConnection.Json(schema, Json.TerraBaseUniqueSchemaRequest);
        return (await connection.SendAsync(HttpMethod.Put, UniqueSchemaPath(collection), body, Json.TerraBaseUniqueSchema, null, cancellationToken).ConfigureAwait(false)).Value;
    }

    /// <summary>
    /// Reads inferred fields and declared unique keys (<c>GET …/schema/{collection}</c>).
    /// Requires a ts-auth project token with <c>databases:read</c> in the current producer contract.
    /// </summary>
    public Task<TerraBaseCollectionSchema> GetSchemaAsync(string collection, CancellationToken cancellationToken = default) =>
        GetInferredSchemaAsync(collection, cancellationToken);

    /// <summary>Reads a document; <see langword="null"/> when it is absent or deleted.</summary>
    public async Task<TerraBaseDocument?> GetDocumentAsync(string collection, string id, CancellationToken cancellationToken = default)
    {
        try
        {
            return (await connection.SendAsync(HttpMethod.Get, DocumentPath(collection, id), null, Json.TerraBaseDocument, null, cancellationToken).ConfigureAwait(false)).Value;
        }
        catch (TerraBaseNotFoundException error) when (error.ErrorCode == "not_found")
        {
            return null;
        }
    }

    /// <summary>Lists a collection's documents in id order.</summary>
    /// <param name="collection">Collection name.</param>
    /// <param name="limit">1 to 1000; TerraBase's default is 50. A page also stops at 8 MiB of JSON.</param>
    /// <param name="cursor">The previous page's <see cref="TerraBaseDocumentPage.NextCursor"/>.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    public async Task<TerraBaseDocumentPage> ListDocumentsAsync(string collection, int? limit = null, string? cursor = null, CancellationToken cancellationToken = default) =>
        (await connection.SendAsync(
            HttpMethod.Get, CollectionPath(collection) + "/documents" + TerraBaseConnection.Query(("limit", TerraBaseConnection.Number(limit)), ("cursor", cursor)),
            null, Json.TerraBaseDocumentPage, null, cancellationToken).ConfigureAwait(false)).Value;

    /// <summary>Creates a document with a TerraBase-generated ULID (<c>POST</c>).</summary>
    public async Task<TerraBaseWriteResult> CreateDocumentAsync(string collection, JsonElement document, CancellationToken cancellationToken = default)
    {
        var body = TerraBaseConnection.Json(document, Json.JsonElement);
        return (await connection.SendAsync(HttpMethod.Post, CollectionPath(collection) + "/documents", body, Json.TerraBaseWriteResult, null, cancellationToken)
            .ConfigureAwait(false)).Value with
        { Created = true };
    }

    /// <summary>
    /// Inserts or replaces a document (<c>PUT</c>). Use <see cref="TerraBasePrecondition.Absent"/> for create-only
    /// semantics or <see cref="TerraBasePrecondition.Matches"/> for optimistic concurrency.
    /// </summary>
    public async Task<TerraBaseWriteResult> PutDocumentAsync(
        string collection, string id, JsonElement document, TerraBasePrecondition? precondition = null, CancellationToken cancellationToken = default)
    {
        var body = TerraBaseConnection.Json(document, Json.JsonElement);
        var (result, status) = await connection.SendAsync(HttpMethod.Put, DocumentPath(collection, id), body, Json.TerraBaseWriteResult, precondition is null ? null : precondition.Apply, cancellationToken)
            .ConfigureAwait(false);
        return result with { Created = status == HttpStatusCode.Created };
    }

    /// <summary>Applies an RFC 7396 merge patch to an existing document (<c>null</c> removes members).</summary>
    public Task<TerraBaseWriteResult> PatchDocumentAsync(
        string collection, string id, JsonElement mergePatch, TerraBasePrecondition? precondition = null, CancellationToken cancellationToken = default) =>
        PatchAsync(collection, id, TerraBaseConnection.Json(mergePatch, Json.JsonElement, MergePatchMediaType), precondition, cancellationToken);

    /// <summary>Applies 1 to 256 patch operations in order to an existing document; a wrong target type throws <see cref="TerraBaseConflictException"/>.</summary>
    public Task<TerraBaseWriteResult> PatchDocumentAsync(
        string collection, string id, IReadOnlyList<TerraBasePatchOperation> operations, TerraBasePrecondition? precondition = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operations);
        return PatchAsync(collection, id, TerraBaseConnection.Json(new TerraBasePatchOperations(operations), Json.TerraBasePatchOperations, OpsPatchMediaType), precondition, cancellationToken);
    }

    /// <summary>Deletes an existing document; returns the tombstone's version. An absent document throws <see cref="TerraBaseNotFoundException"/>.</summary>
    public async Task<TerraBaseWriteResult> DeleteDocumentAsync(
        string collection, string id, TerraBasePrecondition? precondition = null, CancellationToken cancellationToken = default) =>
        (await connection.SendAsync(HttpMethod.Delete, DocumentPath(collection, id), null, Json.TerraBaseWriteResult, precondition is null ? null : precondition.Apply, cancellationToken)
            .ConfigureAwait(false)).Value;

    /// <summary>Filters, sorts and projects a collection's documents, or groups them with <see cref="TerraBaseQuery.Aggregate"/>.</summary>
    public async Task<TerraBaseDocumentPage> QueryAsync(string collection, TerraBaseQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.Sort?.Any(sort => sort.Path == "$score" && (sort.Order != "desc" || query.Sort?.Count != 1 || TerraBaseFilter.SearchCount(query.Filter) != 1)) == true)
        {
            throw new ArgumentException("$score requires descending order as the only sort key and exactly one search predicate.", nameof(query));
        }

        if (query.Sort?.Any(static sort => sort.Nearest is not null) == true
            && (query.Sort.Count != 1 || query.Cursor is not null || query.Limit is < 1 or > 1000 || TerraBaseFilter.SearchCount(query.Filter) != 0))
        {
            throw new ArgumentException("A near sort is the only sort key, without a cursor or search, and limit is 1 to 1000.", nameof(query));
        }

        if (query.Aggregate is { } aggregate)
        {
            if (query.Sort is not null || query.Project is not null || query.Cursor is not null || query.Count is not null)
            {
                throw new ArgumentException("An aggregate takes no sort, project, cursor or count.", nameof(query));
            }

            if (aggregate.GroupBy?.Count > 4
                || (aggregate.Sum?.Count ?? 0) + (aggregate.Avg?.Count ?? 0) + (aggregate.Min?.Count ?? 0) + (aggregate.Max?.Count ?? 0) > 16)
            {
                throw new ArgumentException("An aggregate has at most 4 group_by paths and 16 sum, avg, min and max paths.", nameof(query));
            }
        }

        var body = TerraBaseConnection.Json(query, Json.TerraBaseQuery);
        return (await connection.SendAsync(HttpMethod.Post, CollectionPath(collection) + "/query", body, Json.TerraBaseDocumentPage, null, cancellationToken).ConfigureAwait(false)).Value;
    }

    /// <summary>
    /// Applies 1 to 1024 operations across this database's collections atomically. A failure throws a
    /// <see cref="TerraBaseException"/> whose <see cref="TerraBaseException.OperationIndex"/> names the failing operation.
    /// </summary>
    public async Task<TerraBaseTransactionResult> TransactAsync(IReadOnlyList<TerraBaseTransactionOperation> operations, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operations);
        var body = TerraBaseConnection.Json(new TerraBaseTransaction(operations), Json.TerraBaseTransaction);
        return (await connection.SendAsync(HttpMethod.Post, path + "/transactions", body, Json.TerraBaseTransactionResult, null, cancellationToken).ConfigureAwait(false)).Value;
    }

    private async Task<TerraBaseWriteResult> PatchAsync(
        string collection, string id, HttpContent body, TerraBasePrecondition? precondition, CancellationToken cancellationToken) =>
        (await connection.SendAsync(HttpMethod.Patch, DocumentPath(collection, id), body, Json.TerraBaseWriteResult, precondition is null ? null : precondition.Apply, cancellationToken)
            .ConfigureAwait(false)).Value;

    private string CollectionPath(string collection) => $"{path}/collections/{TerraBaseConnection.Segment(collection, nameof(collection))}";

    private string UniqueSchemaPath(string collection) => $"{path}/schema/{TerraBaseConnection.Segment(collection, nameof(collection))}";

    // TerraBase document ids are canonical ULIDs; reject anything else before sending.
    private string DocumentPath(string collection, string id) =>
        CollectionPath(collection) + "/documents/by-key" + TerraBaseConnection.Query(("id", TerraScaleIds.Validate(id)));
}

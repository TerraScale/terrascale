using System.Net;
using System.Text.Json;

namespace TerraScale.DotNet.TerraBase;

/// <summary>Configures a <see cref="TerraBaseDocumentsClient"/>.</summary>
public sealed record TerraBaseDocumentsClientOptions
{
    /// <summary>HTTPS origin of the TerraBase client listener, for example <c>https://terrabase.internal:8443</c>.</summary>
    public Uri? BaseAddress { get; init; }

    /// <summary>Resolves the HTTPS client origin per request. Set this or BaseAddress, exclusively.</summary>
    public ITerraBaseEndpointResolver? EndpointResolver { get; init; }

    /// <summary>
    /// A TerraBase API key (<c>tb_…</c>) with <c>documents:read</c> and/or <c>documents:write</c>. Its organization
    /// selects the tenant; a key narrowed to a project or database reaches only that one.
    /// </summary>
    public string? ApiKey { get; init; }

    /// <summary>
    /// Supplies the current bearer credential per request. Set this or ApiKey, exclusively.
    /// The provider must be thread-safe and honor cancellation; acquisition shares the request deadline.
    /// </summary>
    public Func<CancellationToken, ValueTask<string>>? AccessTokenProvider { get; init; }

    /// <summary>Deadline of each request, including reading its response. Default 30 seconds, at most 10 minutes.</summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Largest response body read, 64 KiB to 1 GiB. Default 64 MiB, enough for any page TerraBase returns.</summary>
    public int MaxResponseBytes { get; init; } = TerraBaseConnection.DefaultMaxResponseBytes;

    /// <inheritdoc />
    public override string ToString() => $"TerraBase documents client options for {BaseAddress}";
}

/// <summary>
/// Client of the TerraBase Documents API v1 (<c>/v1/projects/{project}/databases/{database}/…</c>), built over a
/// caller-owned <see cref="HttpClient"/> that it never disposes. Thread-safe. Requests are sent once: TerraBase does not
/// deduplicate retried writes yet, so retry a write only when its precondition makes the retry safe.
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
        connection = new TerraBaseConnection(httpClient, options.BaseAddress, options.EndpointResolver, options.ApiKey, nameof(options.ApiKey), options.RequestTimeout, options.MaxResponseBytes,
            options.AccessTokenProvider);
    }

    /// <summary>Addresses one database: <paramref name="databaseName"/> in project <paramref name="projectId"/> of the key's organization.</summary>
    public TerraBaseDatabase Database(string projectId, string databaseName) =>
        new(connection, $"/v1/projects/{TerraBaseConnection.Segment(projectId, nameof(projectId))}/databases/{TerraBaseConnection.Segment(databaseName, nameof(databaseName))}");
}

/// <summary>One TerraBase database of the Documents API. Thread-safe; obtain it from <see cref="TerraBaseDocumentsClient.Database"/>.</summary>
public sealed class TerraBaseDatabase
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

    /// <summary>Creates the database, or returns it when it exists (<c>PUT</c>, idempotent).</summary>
    public async Task<TerraBaseDatabaseInfo> CreateAsync(CancellationToken cancellationToken = default) =>
        (await connection.SendAsync(HttpMethod.Put, path, null, Json.TerraBaseDatabaseInfo, null, cancellationToken).ConfigureAwait(false)).Value;

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

    /// <summary>Creates a document with a TerraBase-generated UUIDv7 id (<c>POST</c>).</summary>
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

    /// <summary>Filters, sorts and projects a collection's documents.</summary>
    public async Task<TerraBaseDocumentPage> QueryAsync(string collection, TerraBaseQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
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

    private string DocumentPath(string collection, string id) => $"{CollectionPath(collection)}/documents/{TerraBaseConnection.Segment(id, nameof(id))}";
}

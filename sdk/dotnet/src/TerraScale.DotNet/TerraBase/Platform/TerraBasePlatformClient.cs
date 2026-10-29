using System.Text.Json.Serialization;

namespace TerraScale.DotNet.TerraBase;

/// <summary>Configures a <see cref="TerraBasePlatformClient"/>.</summary>
public sealed record TerraBasePlatformClientOptions
{
    /// <summary>HTTPS origin of TerraBase's public listener, for example <c>https://api.terrascale.tech</c>.</summary>
    public Uri? BaseAddress { get; init; }

    /// <summary>Resolves the origin per request. Set this or BaseAddress, exclusively.</summary>
    public ITerraBaseEndpointResolver? EndpointResolver { get; init; }

    /// <summary>
    /// A region id such as <c>gru</c>: every request is pinned to that region (<c>X-TerraScale-Region-Pin</c>) and, unless
    /// BaseAddress or EndpointResolver is set, sent to <c>https://api.terrascale.tech</c>. Another region
    /// answers <see cref="TerraBaseWrongRegionException"/> instead of serving it. Unset, requests use the configured origin without a region pin.
    /// </summary>
    public string? Region { get; init; }

    /// <summary>
    /// Supplies the signed-in platform admin's ts-auth access token per request (it must carry <c>platform_role</c>
    /// <c>admin</c>). Required. The provider must be thread-safe and honor cancellation.
    /// </summary>
    public Func<CancellationToken, ValueTask<string>>? AccessTokenProvider { get; init; }

    /// <inheritdoc />
    public override string ToString() => $"TerraBase platform client options for {BaseAddress}";
}

/// <summary>A platform database (<c>ts-auth</c> or <c>ts-dashboard</c>).</summary>
/// <param name="Name">Its name.</param>
/// <param name="Id">Its fixed id.</param>
/// <param name="HomeRegion">The region it is managed in.</param>
/// <param name="ReplicaRegions">The other regions holding a copy.</param>
/// <param name="AllRegions">Whether every serving region holds a copy, including regions that join later.</param>
public sealed record TerraBasePlatformDatabase(string Name, string Id, string HomeRegion, IReadOnlyList<string> ReplicaRegions, [property: JsonRequired] bool AllRegions = false);

/// <summary>The platform databases.</summary>
/// <param name="Items">Every platform database.</param>
public sealed record TerraBasePlatformDatabaseList([property: JsonRequired] IReadOnlyList<TerraBasePlatformDatabase> Items);

/// <summary>
/// Client of TerraBase's read-only explore API over TerraScale's own databases (<c>/v1/platform/databases/…</c>), for the
/// dashboard's platform-admin pages. TerraBase redacts secret-looking fields before they leave and logs every read with its
/// actor. Built over a caller-owned <see cref="HttpClient"/> that it never disposes. Thread-safe.
/// </summary>
public sealed class TerraBasePlatformClient
{
    private const string Root = "/v1/platform/databases";
    private static readonly TerraBaseJsonContext Json = TerraBaseJsonContext.Wire;

    private readonly TerraBaseConnection connection;

    /// <summary>Creates a client and validates <paramref name="options"/>; <see cref="TerraBasePlatformClientOptions.AccessTokenProvider"/> is required.</summary>
    public TerraBasePlatformClient(HttpClient httpClient, TerraBasePlatformClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.AccessTokenProvider is null)
        {
            throw new ArgumentException("AccessTokenProvider is required: the explore API takes a platform admin's ts-auth access token.", nameof(options));
        }

        connection = new TerraBaseConnection(httpClient, options.BaseAddress, options.EndpointResolver, null, nameof(options.AccessTokenProvider),
            TerraBaseConnection.RequestDeadline, TerraBaseConnection.DefaultMaxResponseBytes, options.AccessTokenProvider, region: options.Region);
    }

    /// <summary>Lists the platform databases.</summary>
    public async Task<TerraBasePlatformDatabaseList> ListDatabasesAsync(CancellationToken cancellationToken = default) =>
        (await connection.SendAsync(HttpMethod.Get, Root, null, Json.TerraBasePlatformDatabaseList, null, cancellationToken).ConfigureAwait(false)).Value;

    /// <summary>Lists up to 200 collections of a platform database in name order.</summary>
    public async Task<TerraBaseCollectionPage> ListCollectionsAsync(string database, CancellationToken cancellationToken = default) =>
        (await connection.SendAsync(HttpMethod.Get, Collections(database), null, Json.TerraBaseCollectionPage, null, cancellationToken).ConfigureAwait(false)).Value;

    /// <summary>Lists a collection's documents in id order, redacted.</summary>
    /// <param name="database">The platform database.</param>
    /// <param name="collection">Collection name.</param>
    /// <param name="limit">1 to 1000; TerraBase's default is 50.</param>
    /// <param name="cursor">The previous page's <see cref="TerraBaseDocumentPage.NextCursor"/>.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    public async Task<TerraBaseDocumentPage> ListDocumentsAsync(
        string database, string collection, int? limit = null, string? cursor = null, CancellationToken cancellationToken = default) =>
        (await connection.SendAsync(
            HttpMethod.Get, Documents(database, collection) + TerraBaseConnection.Query(("limit", TerraBaseConnection.Number(limit)), ("cursor", cursor)),
            null, Json.TerraBaseDocumentPage, null, cancellationToken).ConfigureAwait(false)).Value;

    /// <summary>Reads one document, redacted; <see langword="null"/> when it is absent.</summary>
    public async Task<TerraBaseDocument?> GetDocumentAsync(string database, string collection, string id, CancellationToken cancellationToken = default)
    {
        TerraScaleIds.Validate(id);
        try
        {
            return (await connection.SendAsync(
                HttpMethod.Get, Documents(database, collection) + "/by-key" + TerraBaseConnection.Query(("id", id)), null, Json.TerraBaseDocument, null, cancellationToken)
                .ConfigureAwait(false)).Value;
        }
        catch (TerraBaseNotFoundException error) when (error.ErrorCode == "not_found")
        {
            return null;
        }
    }

    private static string Collections(string database) => $"{Root}/{TerraBaseConnection.Segment(database, nameof(database))}/collections";

    private static string Documents(string database, string collection) =>
        $"{Collections(database)}/{TerraBaseConnection.Segment(collection, nameof(collection))}/documents";
}

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
    /// A region id such as <c>gru</c> selects the entry region with <c>X-TerraScale-Region-Pin</c>.
    /// Unless BaseAddress or EndpointResolver is set, requests use <c>https://api.terrascale.tech</c>.
    /// In the unreleased server proxy contract, database calls reaching a region without a copy are served through
    /// one proxy hop by a region that holds the database. This option has no effect on the serving region of those calls:
    /// the proxy removes the pin. Unset, requests use the configured origin without a region pin.
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
/// <param name="HomeRegion">Optional routing hint naming the region that orders writes.</param>
public sealed record TerraBasePlatformDatabase(string Name, string Id, string? HomeRegion = null);

/// <summary>The platform databases.</summary>
/// <param name="Items">Every platform database.</param>
public sealed record TerraBasePlatformDatabaseList([property: JsonRequired] IReadOnlyList<TerraBasePlatformDatabase> Items);

/// <summary>
/// Client of TerraBase's read-only list of TerraScale's own databases (<c>/v1/platform/databases</c>), for the
/// dashboard's platform-admin pages. TerraBase logs every read with its actor. Built over a caller-owned <see cref="HttpClient"/> that it never disposes. Thread-safe.
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
            throw new ArgumentException("AccessTokenProvider is required: the platform API takes a platform admin's ts-auth access token.", nameof(options));
        }

        connection = new TerraBaseConnection(httpClient, options.BaseAddress, options.EndpointResolver, null, nameof(options.AccessTokenProvider),
            TerraBaseConnection.RequestDeadline, TerraBaseConnection.DefaultMaxResponseBytes, options.AccessTokenProvider, region: options.Region);
    }

    /// <summary>Lists the platform databases.</summary>
    public async Task<TerraBasePlatformDatabaseList> ListDatabasesAsync(CancellationToken cancellationToken = default) =>
        (await connection.SendAsync(HttpMethod.Get, Root, null, Json.TerraBasePlatformDatabaseList, null, cancellationToken).ConfigureAwait(false)).Value;


}

using System.Text.Json.Serialization;

namespace TerraScale.DotNet.TerraBase;

public sealed record TerraBasePlatformClientOptions
{
    public Uri? BaseAddress { get; init; }

    public ITerraBaseEndpointResolver? EndpointResolver { get; init; }

    public string? Region { get; init; }

    public Func<CancellationToken, ValueTask<string>>? AccessTokenProvider { get; init; }

    public override string ToString() => $"TerraBase platform client options for {BaseAddress}";
}

public sealed record TerraBasePlatformDatabase(string Name, string Id);

public sealed record TerraBasePlatformDatabaseList([property: JsonRequired] IReadOnlyList<TerraBasePlatformDatabase> Items);

public sealed class TerraBasePlatformClient
{
    private const string Root = "/v1/platform/databases";
    private static readonly TerraBaseJsonContext Json = TerraBaseJsonContext.Wire;

    private readonly TerraBaseConnection connection;

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

    public async Task<TerraBasePlatformDatabaseList> ListDatabasesAsync(CancellationToken cancellationToken = default) =>
        (await connection.SendAsync(HttpMethod.Get, Root, null, Json.TerraBasePlatformDatabaseList, null, cancellationToken).ConfigureAwait(false)).Value;


}

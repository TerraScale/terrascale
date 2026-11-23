using System.Net;
using System.Text.Json.Serialization;

namespace TerraScale.DotNet.TerraBase;

public sealed record TerraBaseRegions([property: JsonPropertyName("regions")] IReadOnlyList<string> Regions);

public sealed class TerraBaseRegionsClient
{
    private readonly TerraBaseConnection connection;
    private readonly HttpClient http;

    public TerraBaseRegionsClient(HttpClient httpClient)
    {
        http = httpClient;
        connection = new TerraBaseConnection(httpClient, TerraBaseRegion.PublicOrigin, null, null, "credential", TimeSpan.FromSeconds(30),
            TerraBaseConnection.DefaultMaxResponseBytes, publicAnonymous: true);
    }

    public Task<TerraBaseRegions> ListRegionsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (http.DefaultRequestHeaders.Authorization is not null || http.DefaultRequestHeaders.Contains("Cookie"))
            throw new InvalidOperationException("Anonymous region catalog requests require an HttpClient without default Authorization or Cookie headers; use a separate credential-free HttpClient with TerraBaseHttp.CreateHandler().");
        return connection.ExchangeAsync("/v1/regions", null, null, async opened =>
        {
            var response = opened.Response;
            if (!response.IsSuccessStatusCode)
                throw await TerraBaseException.FromResponseAsync(response, null, opened.Token).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK)
                throw Invalid(response.StatusCode);
            var bytes = await connection.ReadBodyAsync(response, opened.Token).ConfigureAwait(false);
            var body = TerraBaseConnection.ReadResponse(bytes, TerraBaseRegionsJsonContext.Default.TerraBaseRegions, response.StatusCode);
            if (body.Regions.Count == 0) throw Invalid(response.StatusCode);
            for (var i = 0; i < body.Regions.Count; i++)
                if (!TerraBaseRegion.IsValid(body.Regions[i]) || i > 0 && string.CompareOrdinal(body.Regions[i - 1], body.Regions[i]) >= 0)
                    throw Invalid(response.StatusCode);
            return body;
        }, cancellationToken);
    }

    private static TerraBaseException Invalid(HttpStatusCode status) => new(status, "TerraBase returned an invalid region catalog.", "invalid_response", null);
}

[JsonSourceGenerationOptions(RespectRequiredConstructorParameters = true, RespectNullableAnnotations = true)]
[JsonSerializable(typeof(TerraBaseRegions))]
internal sealed partial class TerraBaseRegionsJsonContext : JsonSerializerContext;

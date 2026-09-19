using System.Text.Json;
using System.Text.Json.Serialization;
using TerraScale.DotNet.Generated;

#pragma warning disable CS1591

namespace TerraScale.DotNet;

/// <summary>
/// Client for TerraScale region metadata: the Atlas region catalog and the
/// serving member's health.
/// </summary>
public sealed class TerraScaleRegionsClient
{
    private readonly TerraScaleClient client;

    internal TerraScaleRegionsClient(TerraScaleClient client)
    {
        this.client = client;
    }

    /// <summary>
    /// Returns the Atlas region catalog (ADR 0050 §8): the orchestrator's
    /// regions with availability and the validated regional member hosts. On
    /// the public product origin the route targets Atlas itself; custom
    /// origins serve it from their configured origin.
    /// </summary>
    public async Task<TerraScaleRegionCatalog> GetAsync(CancellationToken cancellationToken = default)
    {
        var payload = await client.SendMetadataJsonAsync(HttpMethod.Get, "/api/v1/regions", cancellationToken).ConfigureAwait(false);
        return TerraScaleRoutingValidation.TryParseRegionCatalog(payload, client.LoopbackConfiguredOrigin)
            ?? throw new TerraScaleApiException(
                System.Net.HttpStatusCode.OK,
                "The region catalog response is not a valid TerraScale region catalog.",
                errorCode: null,
                requestId: null,
                responseBody: null,
                retryAfter: null);
    }

    /// <summary>Returns the serving member's region and health.</summary>
    public async Task<TerraScaleRegionHealth> HealthAsync(CancellationToken cancellationToken = default)
    {
        var response = await client.SendHealthJsonAsync(
            HttpMethod.Get,
            "/api/v1/health",
            TerraScaleRegionsJsonContext.Default.TerraScaleRegionHealth,
            cancellationToken).ConfigureAwait(false);
        response.Validate();
        return response;
    }
}

public sealed record TerraScaleRegionHealth(
    [property: JsonPropertyName("region"), JsonRequired] string Region,
    [property: JsonPropertyName("healthy"), JsonRequired] bool Healthy,
    [property: JsonPropertyName("version"), JsonRequired] string Version) : IJsonOnDeserialized
{
    internal void Validate()
    {
        if (Region is null || string.IsNullOrWhiteSpace(Version))
        {
            throw new JsonException("TerraScale health responses require non-null region and version fields.");
        }
    }

    void IJsonOnDeserialized.OnDeserialized() => Validate();
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, NumberHandling = JsonNumberHandling.AllowReadingFromString)]
[JsonSerializable(typeof(TerraScaleRegionHealth))]
internal partial class TerraScaleRegionsJsonContext : JsonSerializerContext;

#pragma warning restore CS1591

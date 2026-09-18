using System.Text.Json;
using System.Text.Json.Serialization;
using TerraScale.DotNet.Generated;

#pragma warning disable CS1591

namespace TerraScale.DotNet;

/// <summary>
/// Client for TerraScale region discovery endpoints.
/// </summary>
public sealed class TerraScaleRegionsClient
{
    private readonly TerraScaleClient client;

    internal TerraScaleRegionsClient(TerraScaleClient client)
    {
        this.client = client;
    }

    /// <summary>Returns the serving member's own region and health, so a client discovers where its request landed.</summary>
    public async Task<TerraScaleRegionHealth> GetAsync(CancellationToken cancellationToken = default)
    {
        var response = await client.SendJsonAsync(
            HttpMethod.Get,
            "/api/v1/regions",
            TerraScaleContractJsonContext.Default.HealthResponse,
            cancellationToken).ConfigureAwait(false);
        return new TerraScaleRegionHealth(response.Region, response.Healthy, response.Version);
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

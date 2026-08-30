using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace TerraScale.DotNet;

/// <summary>
/// Client for TerraScale region catalog endpoints.
/// </summary>
public sealed class TerraScaleRegionsClient
{
    private readonly TerraScaleClient client;

    internal TerraScaleRegionsClient(TerraScaleClient client)
    {
        this.client = client;
    }

    /// <summary>
    /// Lists canonical TerraScale deployment regions.
    /// </summary>
    public async Task<IReadOnlyList<TerraScaleRegion>> ListAsync(CancellationToken cancellationToken = default)
    {
        var response = await client.SendAnonymousJsonAsync<TerraScaleRegionListResponse>(
            HttpMethod.Get,
            TerraScaleApiRoutes.RegionsList(),
            body: null,
            cancellationToken).ConfigureAwait(false);

        return response.Regions;
    }

    /// <summary>
    /// Gets the public catalog details for one TerraScale deployment region.
    /// </summary>
    public Task<TerraScaleRegionDetails> GetAsync(
        string regionCode,
        CancellationToken cancellationToken = default)
    {
        TerraScaleRegionCodes.Validate(regionCode, nameof(regionCode));
        return client.SendAnonymousJsonAsync(
            HttpMethod.Get,
            TerraScaleApiRoutes.RegionGet(regionCode),
            TypeInfo<TerraScaleRegionDetails>(),
            cancellationToken);
    }

    private static JsonTypeInfo<T> TypeInfo<T>() =>
        TerraScaleRegionsJsonContext.Default.GetTypeInfo(typeof(T)) as JsonTypeInfo<T>
        ?? throw new InvalidOperationException($"JSON metadata for {typeof(T)} is not available in the regions context.");
}

internal sealed record TerraScaleRegionListResponse(
    [property: JsonRequired] IReadOnlyList<TerraScaleRegion> Regions) : IJsonOnDeserialized
{
    void IJsonOnDeserialized.OnDeserialized()
    {
        if (Regions is null)
        {
            throw new JsonException("TerraScale region lists require a non-null regions field.");
        }
    }
}

/// <summary>
/// Public catalog details for a TerraScale deployment region.
/// </summary>
public sealed record TerraScaleRegionDetails(
    [property: JsonRequired] string Code,
    [property: JsonRequired] string Name,
    [property: JsonRequired] string Country,
    [property: JsonRequired] double Latitude,
    [property: JsonRequired] double Longitude,
    [property: JsonRequired] bool Available) : IJsonOnDeserialized
{
    void IJsonOnDeserialized.OnDeserialized()
    {
        TerraScaleRegionCodes.ValidateResponse(Code);
        if (Name is null || Country is null)
        {
            throw new JsonException("TerraScale region details require non-null name and country fields.");
        }
    }
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, NumberHandling = JsonNumberHandling.AllowReadingFromString)]
[JsonSerializable(typeof(TerraScaleRegionDetails))]
internal partial class TerraScaleRegionsJsonContext : JsonSerializerContext;

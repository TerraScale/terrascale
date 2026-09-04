using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

#pragma warning disable CS1591

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

    /// <summary>Returns the serving member's region and health.</summary>
    public async Task<TerraScaleRegionHealth> ListAsync(CancellationToken cancellationToken = default)
    {
        var response = await client.SendAnonymousJsonAsync<TerraScaleRegionHealth>(
            HttpMethod.Get,
            "/api/v1/regions",
            body: null,
            cancellationToken).ConfigureAwait(false);
        TerraScaleRegionCodes.ValidateResponse(response.Region);
        return response;
    }

    /// <summary>
    /// Gets the public catalog details for one TerraScale deployment region.
    /// </summary>
    public Task<TerraScaleRegionDetails> GetAsync(
        string regionCode,
        CancellationToken cancellationToken = default)
    {
        TerraScaleRegionCodes.Validate(regionCode, nameof(regionCode));
        return Task.FromException<TerraScaleRegionDetails>(new TerraScaleContractGapException("regions.get"));
    }

    private static JsonTypeInfo<T> TypeInfo<T>() =>
        TerraScaleRegionsJsonContext.Default.GetTypeInfo(typeof(T)) as JsonTypeInfo<T>
        ?? throw new InvalidOperationException($"JSON metadata for {typeof(T)} is not available in the regions context.");
}

public sealed record TerraScaleRegionHealth(
    [property: JsonRequired] string Region,
    [property: JsonRequired] bool Healthy) : IJsonOnDeserialized
{
    void IJsonOnDeserialized.OnDeserialized()
    {
        if (Region is null)
        {
            throw new JsonException("TerraScale region responses require a non-null region field.");
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

#pragma warning restore CS1591

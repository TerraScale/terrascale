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

    /// <summary>Returns the anonymous global region catalog and regional hosts.</summary>
    public Task<TerraScaleRegionCatalog> ListAsync(CancellationToken cancellationToken = default) =>
        Gap<TerraScaleRegionCatalog>("regions", cancellationToken);

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

    private static Task<T> Gap<T>(string operation, CancellationToken cancellationToken) =>
        cancellationToken.IsCancellationRequested
            ? Task.FromCanceled<T>(cancellationToken)
            : Task.FromException<T>(new TerraScaleContractGapException(operation));

}

/// <summary>The edge-published global catalog and host addresses.</summary>
public sealed record TerraScaleRegionCatalog(
    [property: JsonPropertyName("regions"), JsonRequired] IReadOnlyList<TerraScaleRegionDetails> Regions,
    [property: JsonPropertyName("hosts"), JsonRequired] IReadOnlyDictionary<string, string> Hosts)
{
    internal void Validate()
    {
        if (Regions is null || Hosts is null || Regions.Count == 0)
            throw new JsonException("The region catalog requires non-empty regions and hosts fields.");

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var region in Regions)
        {
            region.Validate();
            if (!seen.Add(region.Code))
                throw new JsonException($"The region catalog contains duplicate region '{region.Code}'.");
            if (!Hosts.TryGetValue(region.Code, out var host) || string.IsNullOrWhiteSpace(host))
                throw new JsonException($"The region catalog has no host for region '{region.Code}'.");
        }
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

/// <summary>
/// Public catalog details for a TerraScale deployment region.
/// </summary>
public sealed record TerraScaleRegionDetails(
    [property: JsonPropertyName("code"), JsonRequired] string Code,
    [property: JsonPropertyName("name"), JsonRequired] string Name,
    [property: JsonPropertyName("country"), JsonRequired] string Country,
    [property: JsonPropertyName("latitude"), JsonRequired] double Latitude,
    [property: JsonPropertyName("longitude"), JsonRequired] double Longitude,
    [property: JsonPropertyName("available"), JsonRequired] bool Available) : IJsonOnDeserialized
{
    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(Code) || Name is null || Country is null)
        {
            throw new JsonException("TerraScale region details require non-null name and country fields.");
        }
    }

    void IJsonOnDeserialized.OnDeserialized() => Validate();
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, NumberHandling = JsonNumberHandling.AllowReadingFromString)]
[JsonSerializable(typeof(TerraScaleRegionCatalog))]
[JsonSerializable(typeof(TerraScaleRegionHealth))]
[JsonSerializable(typeof(TerraScaleRegionDetails))]
internal partial class TerraScaleRegionsJsonContext : JsonSerializerContext;

#pragma warning restore CS1591

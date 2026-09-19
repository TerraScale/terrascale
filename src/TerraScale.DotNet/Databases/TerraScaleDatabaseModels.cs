using System.Text.Json;
using System.Text.Json.Serialization;

namespace TerraScale.DotNet;

/// <summary>
/// Explicit acknowledgement and stable retry identity required to delete a database.
/// </summary>
public sealed record TerraScaleDeleteDatabaseOptions(string Confirmation, string IdempotencyKey);

/// <summary>
/// Canonical TerraScale region codes accepted by project database placement APIs.
/// </summary>
public static class TerraScaleRegions
{
    /// <summary>
    /// Sao Paulo, Brazil.
    /// </summary>
    public const string Gru = "GRU";

    /// <summary>
    /// San Francisco, United States.
    /// </summary>
    public const string Sfo = "SFO";

    /// <summary>
    /// Frankfurt, Germany.
    /// </summary>
    public const string Fra = "FRA";
}

internal static class TerraScaleRegionCodes
{
    public static bool IsKnown(string? regionCode) =>
        regionCode is TerraScaleRegions.Gru or TerraScaleRegions.Sfo or TerraScaleRegions.Fra;

    public static string Validate(string regionCode, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(regionCode, parameterName);
        if (regionCode is not (TerraScaleRegions.Gru or TerraScaleRegions.Sfo or TerraScaleRegions.Fra))
        {
            throw new ArgumentException(
                $"Region code must be exactly {TerraScaleRegions.Gru}, {TerraScaleRegions.Sfo}, or {TerraScaleRegions.Fra}.",
                parameterName);
        }

        return regionCode;
    }

    public static void ValidateResponse(string? regionCode)
    {
        if (regionCode is not (TerraScaleRegions.Gru or TerraScaleRegions.Sfo or TerraScaleRegions.Fra))
        {
            throw new JsonException(
                $"Region code must be exactly {TerraScaleRegions.Gru}, {TerraScaleRegions.Sfo}, or {TerraScaleRegions.Fra}.");
        }
    }
}

/// <summary>
/// TerraScale deployment region advertised by the public region catalog.
/// </summary>
public sealed record TerraScaleRegion(
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
            throw new JsonException("TerraScale region summaries require non-null name and country fields.");
        }
    }
}

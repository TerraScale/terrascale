using System.Text.Json;
using System.Text.Json.Serialization;

namespace TerraScale.DotNet;

/// <summary>
/// Summary of a TerraScale database.
/// </summary>
public sealed record TerraScaleDatabaseSummary(string DatabaseId, string Name);

/// <summary>
/// Summary of a collection in a TerraScale database.
/// </summary>
public sealed record TerraScaleCollectionSummary(string Name, long Documents);

/// <summary>
/// Request body for creating a TerraScale database.
/// </summary>
public sealed record TerraScaleCreateDatabaseRequest(
    string Name,
    int ReplicationFactor = 1,
    IReadOnlyDictionary<string, string>? Settings = null);

/// <summary>
/// Explicit acknowledgement required to delete a database. <see cref="Confirmation"/> must equal the target database id.
/// </summary>
public sealed record TerraScaleDeleteDatabaseOptions(string Confirmation, bool HardDelete = false);

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

/// <summary>
/// Detailed TerraScale database state.
/// </summary>
public sealed record TerraScaleDatabaseDetails(
    string DatabaseId,
    string Name,
    bool Disabled,
    bool Encrypted,
    string State,
    string LockMode,
    int ReplicationFactor,
    IReadOnlyList<string> Members,
    IReadOnlyList<string> Promotables,
    IReadOnlyList<string> Rehabs,
    string? ChangeToken,
    IReadOnlyDictionary<string, string> Settings);

/// <summary>
/// Result of deleting a TerraScale database.
/// </summary>
public sealed record TerraScaleDeletedDatabase(
    string DatabaseId,
    string Name,
    bool HardDelete,
    bool Deleted);

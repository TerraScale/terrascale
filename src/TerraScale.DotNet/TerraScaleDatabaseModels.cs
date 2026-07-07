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
/// Canonical TerraScale region codes accepted by project database placement APIs.
/// </summary>
public static class TerraScaleRegions
{
    /// <summary>
    /// Sao Paulo, Brazil.
    /// </summary>
    public const string Gru = "gru";

    /// <summary>
    /// San Francisco, United States.
    /// </summary>
    public const string Sfo = "sfo";

    /// <summary>
    /// Frankfurt, Germany.
    /// </summary>
    public const string Fra = "fra";
}

/// <summary>
/// TerraScale deployment region advertised by the public region catalog.
/// </summary>
public sealed record TerraScaleRegion(
    string Code,
    string Name,
    string Country,
    double Latitude,
    double Longitude,
    bool Available);

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

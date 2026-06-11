namespace TerraScale.DotNet;

/// <summary>
/// Summary of a TerraScale database.
/// </summary>
public sealed record TerraScaleDatabaseSummary(string DatabaseId, string Name);

/// <summary>
/// Request body for creating a TerraScale database.
/// </summary>
public sealed record TerraScaleCreateDatabaseRequest(
    string Name,
    int ReplicationFactor = 1,
    IReadOnlyDictionary<string, string>? Settings = null);

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

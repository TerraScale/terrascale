namespace TerraScale.DotNet.TerraBase;

/// <summary>The complete set of regions holding a database, or every serving region.</summary>
/// <param name="Regions">One to nine distinct region ids, with no ordering meaning.</param>
/// <param name="AllRegions">Whether every serving region holds the database; mutually exclusive with Regions.</param>
public sealed record TerraBaseDatabasePlacement(IReadOnlyList<string>? Regions = null, bool? AllRegions = null);

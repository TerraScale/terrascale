namespace TerraScale.Dashboard2.Features.Engine;

/// <summary>One region advertised by the TerraBase region catalog.</summary>
public sealed record EngineRegionRow(
    string Code,
    string Name,
    string Country,
    double Latitude,
    double Longitude,
    bool Available);

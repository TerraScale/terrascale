namespace TerraScale.Dashboard.Features.Engine.ViewModels;

/// <summary>One region advertised by the TerraBase region catalog.</summary>
public sealed record EngineRegionRow(
    string Code,
    string Name,
    string Country,
    double Latitude,
    double Longitude,
    bool Available);

namespace TerraScale.Dashboard.Features.Databases;

/// <summary>A catalog region drawn on the create-dialog map: an airport hub
/// pinned at its coordinate on the 136x80 map canvas.</summary>
public sealed record RegionHub(string Code, string Name, double X, double Y)
{
    public double LeftPct => X / RegionMap.Width * 100;

    public double TopPct => Y / RegionMap.Height * 100;
}

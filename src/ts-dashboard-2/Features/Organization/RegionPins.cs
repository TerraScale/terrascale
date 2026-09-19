namespace TerraScale.Dashboard2.Features.Organization;

/// <summary>Percent positions of the region pins on /brand/region-map.svg,
/// projected from the 136x80 coordinates in Framework/RegionMap.cs.</summary>
public static class RegionPins
{
    public static string Left(string code) => code switch
    {
        "SFO" => "15.44%",
        "FRA" => "52.57%",
        _ => "36.76%",
    };

    public static string Top(string code) => code switch
    {
        "SFO" => "35.73%",
        "FRA" => "25.98%",
        _ => "74.7%",
    };
}

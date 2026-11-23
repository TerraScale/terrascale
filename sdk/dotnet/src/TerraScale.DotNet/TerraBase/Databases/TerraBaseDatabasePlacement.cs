namespace TerraScale.DotNet.TerraBase;

public sealed record TerraBaseDatabasePlacement
{
    private TerraBaseDatabasePlacement(IReadOnlyList<string>? regions, bool? allRegions)
    {
        Regions = regions;
        AllRegions = allRegions;
    }

    internal IReadOnlyList<string>? Regions { get; }
    internal bool? AllRegions { get; }

    public static TerraBaseDatabasePlacement EveryRegion { get; } = new(null, true);

    public static TerraBaseDatabasePlacement In(params ReadOnlySpan<string> regions)
    {
        if (regions.Length is < 1 or > 9) throw new ArgumentException("Pass between one and nine regions.", nameof(regions));
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var region in regions)
        {
            ArgumentException.ThrowIfNullOrEmpty(region, nameof(regions));
            if (!seen.Add(region)) throw new ArgumentException("Regions must be distinct.", nameof(regions));
        }
        return new(Array.AsReadOnly(regions.ToArray()), null);
    }
}

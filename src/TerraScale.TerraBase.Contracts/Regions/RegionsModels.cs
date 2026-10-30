using MessagePack;

namespace TerraScale.TerraBase.Contracts.Regions
{
    /// <summary>The current serving region catalog.</summary>
    [MessagePackObject(true)]
    public sealed record TerraBaseRegions(IReadOnlyList<string> Regions);

    /// <summary>The public liveness answer.</summary>
    [MessagePackObject(true)]
    public sealed record TerraBaseHealth(string Status, string Version, string Region);


}

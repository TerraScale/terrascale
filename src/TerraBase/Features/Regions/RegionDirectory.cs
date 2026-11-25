using System.Globalization;
using TerraBase.Configuration;
using TerraBase.Features.ControlPlane;
using TerraBase.Hosting;
using TerraScale.Contracts.Cluster;

namespace TerraBase.Features.Regions;

internal readonly union RegionSelection(IReadOnlyList<string>, InvalidInput);

public sealed record RegionsBody(string Region, string Machine, IReadOnlyList<string> Regions);

internal sealed class RegionDirectory(TerraBaseOptions options, ControlSnapshots snapshots, IClusterCoordinator coordinator)
{
    public const int MaxRegions = 9;

    private static readonly InvalidInput TooManyRegions =
        new(string.Concat("regions can place a database in at most ", MaxRegions.ToString(CultureInfo.InvariantCulture), " regions"));

    public IReadOnlyList<string> List() =>
        [.. snapshots.Regions.Select(static region => region.Id)
            .Concat(coordinator.Peers.Select(static peer => peer.Peer.Region))
            .Append(options.Region)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)];

    public RegionSelection Check(IReadOnlyList<string>? requested)
    {
        if (requested is null || requested.Count == 0)
        {
            return Checked([options.Region]);
        }

        if (requested.Distinct(StringComparer.Ordinal).Count() > MaxRegions)
        {
            return TooManyRegions;
        }

        var known = List().ToHashSet(StringComparer.Ordinal);
        if (requested.FirstOrDefault(region => !known.Contains(region)) is { } unknown)
        {
            return new InvalidInput(string.Concat("unknown region ", unknown, "; the regions are ", string.Join(", ", known.Order(StringComparer.Ordinal))), "unknown_region");
        }

        return Checked([.. requested.Distinct(StringComparer.Ordinal)]);
    }

    public RegionSelection CheckSet(IReadOnlyList<string>? requested)
    {
        var refused = new InvalidInput("regions must name at least one serving region, without duplicates");
        if (requested is not { Count: > 0 } || requested.Any(static region => !TerraBaseOptions.IsRegion(region))
            || requested.Distinct(StringComparer.Ordinal).Count() != requested.Count)
        {
            return refused;
        }

        if (requested.Count > MaxRegions)
        {
            return TooManyRegions;
        }

        var known = List().ToHashSet(StringComparer.Ordinal);
        return requested.All(region => !known.Contains(region)) ? refused : Check(requested);
    }

    private static RegionSelection Checked(IReadOnlyList<string> regions) => new(regions);
}

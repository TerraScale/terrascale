using TerraBase.Configuration;
using TerraBase.Features.ControlPlane;
using TerraScale.Contracts.Cluster;

namespace TerraBase.Features.Regions;

public sealed record RegionBody(string Id, string Name, string? PublicHost, bool AcceptingWrites, bool Local, int Nodes);

public sealed record RegionsBody(string Region, string Machine, IReadOnlyList<RegionBody> Regions);

internal sealed class RegionDirectory(TerraBaseOptions options, ControlSnapshots snapshots, IClusterCoordinator coordinator)
{
    public IReadOnlyList<RegionBody> List()
    {
        SortedDictionary<string, RegionBody> regions = new(StringComparer.Ordinal);
        foreach (var region in snapshots.Regions)
        {
            regions[region.Id] = new RegionBody(region.Id, region.Name, region.PublicHost, region.AcceptingWrites, region.Id == options.Region, 0);
        }

        foreach (var peer in coordinator.Peers.Select(static peer => peer.Peer.Region).Append(options.Region))
        {
            regions[peer] = regions.TryGetValue(peer, out var known)
                ? known with { Nodes = known.Nodes + 1 }
                : new RegionBody(peer, peer, null, AcceptingWrites: true, peer == options.Region, 1);
        }

        return [.. regions.Values];
    }

    public Result<IReadOnlyList<string>, Validation> Check(IReadOnlyList<string>? requested)
    {
        if (requested is null || requested.Count == 0)
        {
            return Checked([options.Region]);
        }

        var known = List().Select(static region => region.Id).ToHashSet(StringComparer.Ordinal);
        if (requested.FirstOrDefault(region => !known.Contains(region)) is { } unknown)
        {
            return new Validation(string.Concat("unknown region ", unknown, "; the regions are ", string.Join(", ", known.Order(StringComparer.Ordinal))), "unknown_region");
        }

        return Checked([.. requested.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)]);
    }

    private static Result<IReadOnlyList<string>, Validation> Checked(IReadOnlyList<string> regions) => new(regions);
}

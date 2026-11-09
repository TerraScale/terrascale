using TerraBase.Configuration;
using TerraBase.Hosting;
using TerraBase.Features.Replicas;

namespace TerraBase.Features.Regions;

public sealed record RegionPeer(string Region, Uri Address);

public interface IRegionPeers
{
    ValueTask<IReadOnlyList<RegionPeer>> OtherRegionsAsync(CancellationToken cancellationToken);
}

internal sealed class PeersFileRegionPeers(TerraBaseOptions options, RegionalOwners owners) : IRegionPeers
{
    public async ValueTask<IReadOnlyList<RegionPeer>> OtherRegionsAsync(CancellationToken cancellationToken)
    {
        if (options.PeersFile is null)
        {
            return [];
        }

        var nodes = await owners.NodesAsync(cancellationToken).ConfigureAwait(false);
        return [.. nodes.Where(node => node.Region != options.Region).DistinctBy(node => node.Region)
            .Select(node => new RegionPeer(node.Region, node.InternalListener(options.InternalPort)))];
    }
}

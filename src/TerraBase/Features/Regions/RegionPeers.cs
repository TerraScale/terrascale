using TerraBase.Configuration;
using TerraBase.Hosting;

namespace TerraBase.Features.Regions;

public sealed record RegionPeer(string Region, Uri Address);

/// <summary>
/// One node of every other region: its first line in the peers file, the region's founder and data owner. A region's other nodes hold
/// its local writes (R10) and serve no cross-region requests.
/// </summary>
public interface IRegionPeers
{
    ValueTask<IReadOnlyList<RegionPeer>> OtherRegionsAsync(CancellationToken cancellationToken);
}

internal sealed class PeersFileRegionPeers(TerraBaseOptions options, Replicas.RegionalOwners? owners = null) : IRegionPeers
{
    public async ValueTask<IReadOnlyList<RegionPeer>> OtherRegionsAsync(CancellationToken cancellationToken)
    {
        if (options.PeersFile is null)
        {
            return [];
        }

        var nodes = owners is null ? await new PeersFile(options).ListAsync(cancellationToken).ConfigureAwait(false)
            : await owners.NodesAsync(cancellationToken).ConfigureAwait(false);
        return [.. nodes.Where(node => node.Region != options.Region).DistinctBy(node => node.Region)
            .Select(node => new RegionPeer(node.Region, new UriBuilder(Uri.UriSchemeHttp, node.Address.ToString(), options.InternalPort).Uri))];
    }
}

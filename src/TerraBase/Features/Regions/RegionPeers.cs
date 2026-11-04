using TerraBase.Configuration;
using TerraBase.Hosting;

namespace TerraBase.Features.Regions;

public sealed record RegionPeer(string Region, Uri Address);

public interface IRegionPeers
{
    ValueTask<IReadOnlyList<RegionPeer>> OtherRegionsAsync(CancellationToken cancellationToken);
}

internal sealed class PeersFileRegionPeers(TerraBaseOptions options) : IRegionPeers
{
    public async ValueTask<IReadOnlyList<RegionPeer>> OtherRegionsAsync(CancellationToken cancellationToken)
    {
        if (options.PeersFile is null)
        {
            return [];
        }

        var nodes = await new PeersFile(options).ListAsync(cancellationToken).ConfigureAwait(false);
        return [.. nodes.Where(node => node.Region != options.Region)
            .Select(node => new RegionPeer(node.Region, new UriBuilder(Uri.UriSchemeHttp, node.Address.ToString(), options.InternalPort).Uri))];
    }
}

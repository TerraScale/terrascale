using TerraBase.Configuration;
using TerraBase.Hosting;
using TerraBase.Features.Replicas;

namespace TerraBase.Features.LocalAck;

/// <summary>Another node of this node's own region, reached on its internal listener.</summary>
internal sealed record RegionPartner(string Name, Uri Internal);

/// <summary>The other nodes of this node's region, in peers-file order; empty in a one-node region.</summary>
internal interface IRegionPartners
{
    ValueTask<IReadOnlyList<RegionPartner>> ListAsync(CancellationToken cancellationToken);
}

/// <summary>The ts-orch peers file (<c>TS_PEERS_FILE</c>): every line of this region except this node.</summary>
internal sealed class PeersFileRegionPartners(TerraBaseOptions options) : IRegionPartners
{
    public async ValueTask<IReadOnlyList<RegionPartner>> ListAsync(CancellationToken cancellationToken)
    {
        if (options.PeersFile is null)
        {
            return [];
        }

        var nodes = await new PeersFile(options).ListAsync(cancellationToken).ConfigureAwait(false);
        return [.. nodes.Where(node => node.Region == options.Region && node.Name != options.MachineId)
            .Select(node => new RegionPartner(node.Name, node.InternalListener(options.InternalPort)))];
    }
}

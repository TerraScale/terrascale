using TerraBase.Configuration;
using TerraBase.Hosting;
using TerraBase.Features.Replicas;

namespace TerraBase.Features.LocalAck;

internal sealed record RegionPartner(string Name, Uri Internal);

internal interface IRegionPartners
{
    ValueTask<IReadOnlyList<RegionPartner>> ListAsync(CancellationToken cancellationToken);
}

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

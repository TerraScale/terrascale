using System.Globalization;
using TerraBase.Configuration;
using TerraBase.Hosting;

namespace TerraBase.Features.Management;

/// <summary>The internal listeners of the cluster's other nodes, for read fan-out.</summary>
internal interface IRemoteRegions
{
    ValueTask<IReadOnlyList<Uri>> InternalListenersAsync(CancellationToken cancellationToken);
}

/// <summary>The peers file's nodes, minus this node, at the cluster's internal port.</summary>
internal sealed class PeersFileRemoteRegions(TerraBaseOptions options) : IRemoteRegions
{
    public async ValueTask<IReadOnlyList<Uri>> InternalListenersAsync(CancellationToken cancellationToken)
    {
        if (options.PeersFile is null)
        {
            return [];
        }

        var nodes = await new PeersFile(options).ListAsync(cancellationToken).ConfigureAwait(false);
        return [.. nodes.Where(node => node.Name != options.MachineId)
            .Select(node => new Uri("http://" + node.Address + ":" + options.InternalPort.ToString(CultureInfo.InvariantCulture)))];
    }
}

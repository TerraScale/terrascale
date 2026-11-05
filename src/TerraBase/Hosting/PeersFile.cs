using System.Collections.Immutable;
using System.Net;
using System.Net.Sockets;
using TerraBase.Configuration;

namespace TerraBase.Hosting;

internal sealed class PeersFile(TerraBaseOptions options)
{
    public async Task<ImmutableArray<ServingNode>> ListAsync(CancellationToken cancellationToken)
    {
        var lines = await File.ReadAllLinesAsync(options.PeersFile!, cancellationToken).ConfigureAwait(false);
        return [.. Parse(lines).Select(peer => new ServingNode(peer.Node, peer.Region, peer.Address))];
    }

    public static IPAddress SelfAddress(TerraBaseOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        try
        {
            return Parse(File.ReadAllLines(options.PeersFile!)).First(peer => peer.Node == options.MachineId && peer.Region == options.Region).Address;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            throw new TerraBaseConfigurationException("TS_PEERS_FILE must be readable and list this node (TS_NODE) in its region (TS_REGION)");
        }
    }

    private static IEnumerable<(string Node, string Region, IPAddress Address)> Parse(IEnumerable<string> lines) => lines
        .Select(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        .Where(entry => entry is [var node, var region, var address] && TerraBaseOptions.IsMachineId(node) && TerraBaseOptions.IsRegion(region)
            && IPAddress.TryParse(address, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork)
        .Select(entry => (entry[0], entry[1], IPAddress.Parse(entry[2])))
        .DistinctBy(peer => peer.Item1);

}

internal sealed record ServingNode(string Name, string Region, IPAddress Address);

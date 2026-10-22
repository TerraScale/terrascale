using System.Collections.Immutable;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using TerraBase.Configuration;

namespace TerraBase.Engine.Cluster;

/// <summary>
/// The members ts-orch lists in <c>TS_PEERS_FILE</c>: one <c>&lt;node&gt; &lt;region&gt; &lt;ipv4&gt;</c> line per region the
/// service runs in and whose node is up, this member's included. ts-orch replaces the file whenever its mesh view changes.
/// Each member's internal listener is reached at <c>http://&lt;ipv4&gt;:&lt;internal port&gt;/</c> over the private mesh.
/// </summary>
internal sealed class PeersFile(TerraBaseOptions options) : IMemberDirectory
{
    public async Task<ImmutableArray<ClusterMember>> ListAsync(CancellationToken cancellationToken)
    {
        var lines = await File.ReadAllLinesAsync(options.PeersFile!, cancellationToken).ConfigureAwait(false);
        return [.. Parse(lines).Select(peer => new ClusterMember(peer.Node, Address(peer.Address), peer.Region, string.Empty))];
    }

    /// <summary>The address this member's internal listener binds: its own line's, read once at startup.</summary>
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

    // Malformed lines are skipped: a member that cannot be named or reached is not a member.
    private static IEnumerable<(string Node, string Region, IPAddress Address)> Parse(IEnumerable<string> lines) => lines
        .Select(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        .Where(entry => entry is [var node, var region, var address] && TerraBaseOptions.IsMachineId(node) && TerraBaseOptions.IsRegion(region)
            && IPAddress.TryParse(address, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork)
        .Select(entry => (entry[0], entry[1], IPAddress.Parse(entry[2])))
        .DistinctBy(peer => peer.Item1);

    private string Address(IPAddress address) => string.Create(CultureInfo.InvariantCulture, $"http://{address}:{options.InternalPort}/");
}

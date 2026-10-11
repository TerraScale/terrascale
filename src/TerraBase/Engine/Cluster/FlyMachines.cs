using System.Collections.Immutable;
using System.Globalization;
using System.Net;
using TerraBase.Configuration;

namespace TerraBase.Engine.Cluster;

/// <summary>
/// The started machines of this Fly app, from the TXT record <c>vms.&lt;app&gt;.internal</c> of Fly's internal DNS:
/// comma-separated <c>&lt;machine id&gt; &lt;region&gt;</c> entries. Each member's peer listener is reached at
/// <c>&lt;machine id&gt;.vm.&lt;app&gt;.internal</c>.
/// </summary>
internal sealed class FlyMachines(TerraBaseOptions options) : IMemberDirectory, IDisposable
{
    private readonly DnsResolver _resolver = new();

    public async Task<ImmutableArray<ClusterMember>> ListAsync(CancellationToken cancellationToken)
    {
        var app = options.App!;
        var result = await _resolver.ResolveTxtAsync($"vms.{app}.internal", cancellationToken).ConfigureAwait(false);
        if (result.ResponseCode is not (DnsResponseCode.NoError or DnsResponseCode.NxDomain))
        {
            throw new IOException($"Fly's internal DNS answered {result.ResponseCode} for the app's machines");
        }

        // One record's strings form one value; long values are split into 255-byte strings.
        return [.. result.Records.SelectMany(record => string.Concat(record.Values).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            .Select(entry => entry.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Where(entry => entry is [var id, var region] && TerraBaseOptions.IsMachineId(id) && TerraBaseOptions.IsRegion(region))
            .Select(entry => new ClusterMember(entry[0], Address(app, entry[0]), entry[1], string.Empty))
            .DistinctBy(member => member.Name)];
    }

    private string Address(string app, string id) => options.PeerPort is { } port
        ? string.Create(CultureInfo.InvariantCulture, $"https://{id}.vm.{app}.internal:{port}/")
        : string.Empty;

    public void Dispose() => _resolver.Dispose();
}

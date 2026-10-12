using System.Collections.Immutable;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Configuration;
using TerraBase.Engine.Cluster;

namespace TerraBase.Testing;

/// <summary>
/// A test server's identity and cell. In production Fly supplies both (<c>FLY_*</c> variables and its internal DNS); a test
/// names them with these harness keys instead, which never reach the server as settings:
/// <list type="bullet">
/// <item><c>region</c> and <c>node-name</c>: the machine's region and ID.</item>
/// <item><c>peers</c>: <c>name=host:port</c> members of the cell, this one included; <c>peer-addresses</c>: the other members' peer listeners.</item>
/// <item><c>catalog-primary</c>: the catalog primary this server follows, a member of <c>peers</c> or a peer listener; naming this server makes it the primary.</item>
/// <item><c>wan-regions</c>: <c>region=cell</c> entries naming every region of a test's <see cref="FakeRegionNetwork"/> and its cell.</item>
/// </list>
/// The harness applies the identity to the registered options and registers a static membership of the listed members,
/// unless the test registered a <see cref="FakeMemberStore"/>: that one lists the server while it runs, as Fly's DNS does.
/// </summary>
internal sealed class TestTopology
{
    private static readonly string[] Keys = ["region", "node-name", "peers", "peer-addresses", "catalog-primary", "wan-regions"];

    // The region a catalog primary named only by its address is listed in, so a follower reading the DNS finds it there.
    private const string PrimaryRegion = "catalog-primary";

    private readonly string _region;
    private readonly string? _machineId;
    private readonly ImmutableArray<ClusterMember> _members;
    private readonly ClusterMember? _catalogPrimary;
    private readonly string? _ownAddress;
    private readonly bool _static;
    private readonly Dictionary<string, string> _cells = [with(StringComparer.Ordinal)];
    private FakeMemberStore? _store;
    private string _self = "local";

    private TestTopology(Dictionary<string, string> values)
    {
        _region = values.GetValueOrDefault("region", "test-region");
        _machineId = values.GetValueOrDefault("node-name");
        var self = _machineId ?? "local";
        var members = new List<ClusterMember>();
        if (values.TryGetValue("peers", out var peers))
        {
            foreach (var entry in peers.Split(',', StringSplitOptions.TrimEntries))
            {
                var separator = entry.IndexOf('=', StringComparison.Ordinal);
                members.Add(new ClusterMember(entry[..separator], $"http://{entry[(separator + 1)..]}/", _region, string.Empty));
            }
        }

        if (values.TryGetValue("peer-addresses", out var addresses))
        {
            members.AddRange(addresses.Split(',', StringSplitOptions.TrimEntries)
                .Select((address, index) => new ClusterMember($"peer-{index}", new Uri(address).ToString(), _region, string.Empty)));
        }

        if (values.TryGetValue("catalog-primary", out var primary) && primary != self)
        {
            _catalogPrimary = members.FirstOrDefault(member => member.Name == primary)
                ?? new ClusterMember("catalog-primary", new Uri(primary).ToString(), PrimaryRegion, string.Empty);
        }

        foreach (var entry in values.GetValueOrDefault("wan-regions", "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            _cells[entry[..entry.IndexOf('=', StringComparison.Ordinal)]] = entry[(entry.IndexOf('=', StringComparison.Ordinal) + 1)..];
        }

        _ownAddress = members.FirstOrDefault(member => member.Name == self)?.PeerAddress;
        _members = [.. members.Where(member => member.Name != self)];
        _static = members.Count > 0 || values.ContainsKey("catalog-primary");
    }

    /// <summary>Takes the topology keys out of <paramref name="values"/>, leaving only settings.</summary>
    public static TestTopology Take(Dictionary<string, string> values)
    {
        var topology = new TestTopology(values);
        foreach (var key in Keys)
        {
            _ = values.Remove(key);
        }

        return topology;
    }

    /// <summary>Gives the server's registered options this identity; before the test's own service changes.</summary>
    public void ApplyIdentity(IServiceCollection services)
    {
        var registered = services.Single(service => service.ServiceType == typeof(TerraBaseOptions));
        var options = (TerraBaseOptions)registered.ImplementationInstance!;
        var identified = options with
        {
            Region = _region,
            MachineId = _machineId ?? options.MachineId,
            PrimaryRegion = _catalogPrimary?.Region ?? _region,
            CatalogFollower = _catalogPrimary is not null,
        };
        _self = identified.MachineId;
        services[services.IndexOf(registered)] = ServiceDescriptor.Singleton(identified);
    }

    /// <summary>Registers the membership; after the test's own service changes.</summary>
    public void ApplyMembership(IServiceCollection services)
    {
        BusRegionPeers.Register(services, _cells, _region, _self);
        if (services.LastOrDefault(service => service.ServiceType == typeof(IMemberDirectory)) is { ImplementationInstance: FakeMemberStore store } registered)
        {
            _store = store;
            services[services.IndexOf(registered)] = ServiceDescriptor.Singleton(store.For(_self));
            if (_catalogPrimary?.Region == PrimaryRegion)
            {
                store.Join(_catalogPrimary);
            }
        }
        else if (_static)
        {
            services.AddSingleton<IMembership>(provider => new StaticMembership(provider.GetRequiredService<TerraBaseOptions>(), _members, _catalogPrimary));
        }
    }

    /// <summary>Lists the started server in the test's <see cref="FakeMemberStore"/>, with its peer listener.</summary>
    public void Started(Uri? peerAddress) =>
        _store?.Join(new ClusterMember(_self, _ownAddress ?? peerAddress?.ToString() ?? string.Empty, _region, string.Empty));

    /// <summary>Drops the stopping server from the test's <see cref="FakeMemberStore"/>.</summary>
    public void Stopping() => _store?.Leave(_self);
}

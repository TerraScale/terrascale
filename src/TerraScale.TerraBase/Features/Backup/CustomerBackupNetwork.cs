using System.Net;
using System.Net.Sockets;
using TerraBase.Errors;

namespace TerraBase.Features.Backup;

// Customer endpoints are untrusted: check DNS at connect time, disable proxies and redirects, and connect only to
// the addresses just checked, so a DNS rebind cannot reach node services or instance metadata.
internal static class CustomerBackupNetwork
{
    // IANA IPv4/IPv6 Special-Purpose Address Registries, reviewed 2026-10-09:
    // https://www.iana.org/assignments/iana-ipv4-special-registry/
    // https://www.iana.org/assignments/iana-ipv6-special-registry/
    // Block non-global/deprecated assignments, preserving their explicitly globally reachable subprefixes.
    private static readonly IPNetwork[] DeniedNetworks =
    [
        IPNetwork.Parse("0.0.0.0/8"), IPNetwork.Parse("10.0.0.0/8"), IPNetwork.Parse("100.64.0.0/10"),
        IPNetwork.Parse("127.0.0.0/8"), IPNetwork.Parse("169.254.0.0/16"), IPNetwork.Parse("172.16.0.0/12"),
        IPNetwork.Parse("192.0.0.0/24"), IPNetwork.Parse("192.0.2.0/24"), IPNetwork.Parse("192.88.99.0/24"),
        IPNetwork.Parse("192.168.0.0/16"), IPNetwork.Parse("198.18.0.0/15"), IPNetwork.Parse("198.51.100.0/24"),
        IPNetwork.Parse("203.0.113.0/24"), IPNetwork.Parse("224.0.0.0/4"), IPNetwork.Parse("240.0.0.0/4"),
        IPNetwork.Parse("2001::/23"), IPNetwork.Parse("2001:db8::/32"), IPNetwork.Parse("2002::/16"),
        IPNetwork.Parse("3fff::/20"),
    ];
    private static readonly IPNetwork[] GloballyReachableExceptions =
    [
        IPNetwork.Parse("192.0.0.9/32"), IPNetwork.Parse("192.0.0.10/32"),
        IPNetwork.Parse("2001:1::1/128"), IPNetwork.Parse("2001:1::2/128"), IPNetwork.Parse("2001:1::3/128"),
        IPNetwork.Parse("2001:3::/32"), IPNetwork.Parse("2001:4:112::/48"),
        IPNetwork.Parse("2001:20::/28"), IPNetwork.Parse("2001:30::/28"),
    ];
    private static readonly IPNetwork GlobalUnicastV6 = IPNetwork.Parse("2000::/3");

    public static Valid<Uri> Endpoint(string? value) =>
        value is { Length: <= 2048 } && Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps && uri.UserInfo.Length == 0 && uri.PathAndQuery == "/" && uri.Fragment.Length == 0
        && uri.HostNameType != UriHostNameType.Unknown && uri.Host != "localhost"
        && (!IPAddress.TryParse(uri.Host.Trim('[', ']'), out var address) || Public(address))
            ? uri : new Validation("endpoint must be a public https://host[:port] URL");

    public static SocketsHttpHandler Handler(ICustomerBackupDns dns, ICustomerBackupConnector connector) => new()
    {
        AllowAutoRedirect = false,
        UseProxy = false,
        ConnectCallback = (context, cancellationToken) => ConnectAsync(context, dns, connector, cancellationToken),
    };

    private static async ValueTask<Stream> ConnectAsync(
        SocketsHttpConnectionContext context, ICustomerBackupDns dns, ICustomerBackupConnector connector, CancellationToken cancellationToken)
    {
        var addresses = await dns.ResolveAsync(context.DnsEndPoint.Host, cancellationToken).ConfigureAwait(false);
        if (addresses.Length is 0 or > 32 || addresses.Any(address => !Public(address)))
        {
            throw new HttpRequestException("customer backup endpoint must resolve to public addresses");
        }

        cancellationToken.ThrowIfCancellationRequested();
        return await connector.ConnectAsync(addresses, context.DnsEndPoint.Port, cancellationToken).ConfigureAwait(false);
    }

    private static bool Public(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        // Keep IPv6 restricted to global unicast: NAT64 and other transition addresses can carry internal IPv4.
        if (address.AddressFamily != AddressFamily.InterNetwork
            && (address.AddressFamily != AddressFamily.InterNetworkV6 || !GlobalUnicastV6.Contains(address)))
        {
            return false;
        }

        return !DeniedNetworks.Any(network => network.Contains(address))
            || GloballyReachableExceptions.Any(network => network.Contains(address));
    }
}

/// <summary>Resolves the customer host once for each new connection.</summary>
internal interface ICustomerBackupDns
{
    Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken);
}

internal sealed class CustomerBackupDns : ICustomerBackupDns
{
    public Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken) => Dns.GetHostAddressesAsync(host, cancellationToken);
}

/// <summary>Connects only to the addresses already checked by the customer handler, without resolving again.</summary>
internal interface ICustomerBackupConnector
{
    ValueTask<Stream> ConnectAsync(IPAddress[] addresses, int port, CancellationToken cancellationToken);
}

internal sealed class CustomerBackupConnector : ICustomerBackupConnector
{
    public async ValueTask<Stream> ConnectAsync(IPAddress[] addresses, int port, CancellationToken cancellationToken)
    {
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(addresses, port, cancellationToken).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}

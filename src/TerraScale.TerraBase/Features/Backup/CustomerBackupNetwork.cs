using System.Net;
using System.Net.Sockets;
using TerraBase.Errors;

namespace TerraBase.Features.Backup;

// Customer endpoints are untrusted: check DNS at connect time, disable proxies and redirects, and connect only to
// the addresses just checked, so a DNS rebind cannot reach node services or instance metadata.
internal static class CustomerBackupNetwork
{
    public static Valid<Uri> Endpoint(string? value) =>
        value is { Length: <= 2048 } && Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps && uri.UserInfo.Length == 0 && uri.PathAndQuery == "/" && uri.Fragment.Length == 0
        && uri.HostNameType != UriHostNameType.Unknown && uri.Host != "localhost"
        && (!IPAddress.TryParse(uri.Host.Trim('[', ']'), out var address) || Public(address))
            ? uri : new Validation("endpoint must be a public https://host[:port] URL");

    public static SocketsHttpHandler Handler() => new()
    {
        AllowAutoRedirect = false,
        UseProxy = false,
        ConnectCallback = ConnectAsync,
    };

    private static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, cancellationToken).ConfigureAwait(false);
        if (addresses.Length == 0 || addresses.Any(address => !Public(address)))
        {
            throw new HttpRequestException("customer backup endpoint must resolve to public addresses");
        }

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(addresses, context.DnsEndPoint.Port, cancellationToken).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static bool Public(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IPAddress.IsLoopback(address))
        {
            return false;
        }

        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            // Only globally routable unicast; reject transition mechanisms that can carry an internal IPv4 address.
            return (bytes[0] & 0xe0) == 0x20 && !(bytes[0] == 0x20 && bytes[1] == 0x02)
                && !(bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0 && bytes[3] == 0);
        }

        return bytes[0] is not (0 or 10 or 127) && bytes[0] < 224
            && !(bytes[0] == 169 && bytes[1] == 254) && !(bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
            && !(bytes[0] == 192 && bytes[1] == 168) && !(bytes[0] == 100 && bytes[1] is >= 64 and <= 127)
            && !(bytes[0] == 198 && bytes[1] is 18 or 19);
    }
}

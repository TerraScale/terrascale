using System.Net;
using System.Net.Sockets;

namespace TerraScale.Authentication.Features.Http;

public static class PublicAddresses
{
    public static readonly TimeSpan ConnectionLifetime = TimeSpan.FromMinutes(2);

    public static SocketsHttpHandler Handler() => new()
    {
        AllowAutoRedirect = false,
        ConnectTimeout = TimeSpan.FromSeconds(10),
        PooledConnectionLifetime = ConnectionLifetime,
        UseProxy = false,
        ConnectCallback = async (context, cancellationToken) =>
        {
            var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, cancellationToken).ConfigureAwait(false);
            var address = addresses.Length > 0 && addresses.All(IsPublic) ? addresses[0] : throw new HttpRequestException("The host has no public address.");
            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), cancellationToken).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        },
    };

    public static bool IsPublic(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            return !(b[0] is 0 or 10 or 127 or >= 224
                || b[0] == 100 && (b[1] & 0xC0) == 64
                || b[0] == 169 && b[1] == 254
                || b[0] == 172 && (b[1] & 0xF0) == 16
                || b[0] == 192 && b[1] == 168
                || b[0] == 192 && b[1] == 0 && b[2] is 0 or 2
                || b[0] == 198 && b[1] is 18 or 19
                || b[0] == 198 && b[1] == 51 && b[2] == 100
                || b[0] == 203 && b[1] == 0 && b[2] == 113);
        }

        var bytes = address.GetAddressBytes();
        return address.AddressFamily == AddressFamily.InterNetworkV6 && (bytes[0] & 0xE0) == 0x20
            && !(bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0x0d && bytes[3] == 0xb8);
    }
}

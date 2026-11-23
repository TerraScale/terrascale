using System.Net;
using System.Net.Sockets;

namespace TerraBase.Features.Backup;

internal static class BackupDestinationNetwork
{
    private static readonly IPNetwork[] DeniedNetworks =
    [
        IPNetwork.Parse("0.0.0.0/8"), IPNetwork.Parse("10.0.0.0/8"), IPNetwork.Parse("100.64.0.0/10"),
        IPNetwork.Parse("127.0.0.0/8"), IPNetwork.Parse("169.254.0.0/16"), IPNetwork.Parse("172.16.0.0/12"),
        IPNetwork.Parse("192.0.0.0/24"), IPNetwork.Parse("192.0.2.0/24"), IPNetwork.Parse("192.88.99.0/24"),
        IPNetwork.Parse("192.168.0.0/16"), IPNetwork.Parse("198.18.0.0/15"), IPNetwork.Parse("198.51.100.0/24"),
        IPNetwork.Parse("203.0.113.0/24"), IPNetwork.Parse("224.0.0.0/4"), IPNetwork.Parse("240.0.0.0/4"),
        IPNetwork.Parse("2001::/23"), IPNetwork.Parse("2001:db8::/32"), IPNetwork.Parse("2002::/16"), IPNetwork.Parse("3fff::/20"),
    ];

    private static readonly IPNetwork[] GloballyReachableExceptions =
    [
        IPNetwork.Parse("192.0.0.9/32"), IPNetwork.Parse("192.0.0.10/32"),
        IPNetwork.Parse("2001:1::1/128"), IPNetwork.Parse("2001:1::2/128"), IPNetwork.Parse("2001:1::3/128"),
        IPNetwork.Parse("2001:3::/32"), IPNetwork.Parse("2001:4:112::/48"),
        IPNetwork.Parse("2001:20::/28"), IPNetwork.Parse("2001:30::/28"),
    ];

    private static readonly IPNetwork GlobalUnicastV6 = IPNetwork.Parse("2000::/3");

    public static Uri? Endpoint(string? value) =>
        value is { Length: <= 2048 } && Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps && uri.UserInfo.Length == 0 && uri.PathAndQuery == "/" && uri.Fragment.Length == 0
        && uri.HostNameType != UriHostNameType.Unknown && uri.Host != "localhost"
        && (!IPAddress.TryParse(uri.Host.Trim('[', ']'), out var address) || IsPublic(address))
            ? uri : null;

    public static HttpMessageHandler Handler() => new ResponseLimits
    {
        InnerHandler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            UseCookies = false,
            ConnectCallback = ConnectAsync,
        },
    };

    private static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, cancellationToken);
        if (addresses.Length is 0 or > 32 || addresses.Any(static address => !IsPublic(address)))
        {
            throw new HttpRequestException("customer backup endpoint must resolve to public addresses");
        }

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        var transferred = false;
        try
        {
            await socket.ConnectAsync(addresses, context.DnsEndPoint.Port, cancellationToken);
            var stream = new NetworkStream(socket, ownsSocket: true);
            transferred = true;
            return stream;
        }
        finally
        {
            if (!transferred)
            {
                socket.Dispose();
            }
        }
    }

    private static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (address.AddressFamily != AddressFamily.InterNetwork
            && (address.AddressFamily != AddressFamily.InterNetworkV6 || !GlobalUnicastV6.Contains(address)))
        {
            return false;
        }

        return !DeniedNetworks.Any(network => network.Contains(address))
            || GloballyReachableExceptions.Any(network => network.Contains(address));
    }

    private sealed class ResponseLimits : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri is not { Scheme: "https" } address)
            {
                throw new HttpRequestException("customer backup requests require HTTPS");
            }

            var response = await base.SendAsync(request, cancellationToken);
            if (response.IsSuccessStatusCode && request.Method == HttpMethod.Get && address.Query.Length == 0)
            {
                return response;
            }

            var buffered = false;
            try
            {
                await response.Content.LoadIntoBufferAsync(64 * 1024, cancellationToken);
                buffered = true;
                return response;
            }
            finally
            {
                if (!buffered)
                {
                    response.Dispose();
                }
            }
        }
    }
}

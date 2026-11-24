using System.Net;
using System.Net.Sockets;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Configuration;
using TerraScale.Contracts.Consistency;
using TerraScale.DotNet.TerraBase;
using ZLogger;

namespace TerraScale.Authentication.Hosting;

public static class TerraBaseTransport
{
    public const string PeerSecretVariable = "TERRABASE_PEER_SECRET";

    public const string PeerCredentialHeader = "X-TerraBase-Peer-Credential";

    private static readonly TimeSpan DialTimeout = TimeSpan.FromSeconds(2);

    private static readonly TimeSpan ConnectionLifetime = TimeSpan.FromSeconds(30);

    public delegate Task<IPAddress[]> HostResolver(string host, CancellationToken cancellationToken);

    public static Uri ClientOrigin(Uri origin)
    {
        ArgumentNullException.ThrowIfNull(origin);
        return origin.Scheme == Uri.UriSchemeHttp
            && origin.UserInfo.Length == 0
            && origin.AbsolutePath == "/"
            && origin.Query.Length == 0
            && origin.Fragment.Length == 0
            && IsInternalNonLoopbackHost(origin.DnsSafeHost)
            ? new UriBuilder(Uri.UriSchemeHttp, "127.0.0.1", origin.Port).Uri
            : origin;
    }

    public static HttpMessageHandler CreateHandler(IReadOnlyList<DnsEndPoint> otherRegions, ILogger logger, HostResolver? resolve = null, string? peerSecret = null, CallerCredential? caller = null, DnsEndPoint? internalTarget = null)
    {
        resolve ??= Dns.GetHostAddressesAsync;
        ArgumentNullException.ThrowIfNull(otherRegions);
        ArgumentNullException.ThrowIfNull(logger);
        peerSecret = string.IsNullOrEmpty(peerSecret) ? null : Validated(peerSecret);
        var handler = TerraBaseHttp.CreateHandler();
        if (otherRegions.Count > 0)
        {
            handler.PooledConnectionLifetime = ConnectionLifetime;
        }
        handler.KeepAlivePingDelay = TimeSpan.FromSeconds(5);
        handler.KeepAlivePingTimeout = TimeSpan.FromSeconds(5);
        handler.EnableMultipleHttp2Connections = true;
        handler.ConnectCallback = async (context, cancellationToken) =>
        {
            var primary = internalTarget is not null
                && context.DnsEndPoint.Port == internalTarget.Port
                && string.Equals(context.DnsEndPoint.Host, "127.0.0.1", StringComparison.Ordinal)
                ? internalTarget
                : context.DnsEndPoint;
            var tried = new HashSet<IPEndPoint>();
            foreach (var host in otherRegions.Prepend(primary))
            {
                foreach (var endpoint in await EndPointsAsync(resolve, host, cancellationToken))
                {
                    if (tried.Add(endpoint) && await ConnectAsync(endpoint, cancellationToken) is { } stream)
                    {
                        if (host != primary)
                        {
                            logger.ZLogWarning($"TerraBase in this region ({primary.Host:@Region}) did not answer; storage uses {endpoint:@Machine} ({host.Host:@Via}) until the connection is replaced");
                        }

                        return stream;
                    }
                }
            }

            logger.ZLogWarning($"No TerraBase machine answered at {primary.Host:@Region} or at any of {otherRegions.Count:@Fallbacks} fallbacks; storage fails closed");
            throw new HttpRequestException(HttpRequestError.ConnectionError, $"No TerraBase machine answered at {primary.Host}");
        };
        HttpMessageHandler pipeline = new StrongConsistency { InnerHandler = new FailClosed(handler) };
        if (peerSecret is not null)
        {
            pipeline = new PeerCredentialHandler(peerSecret) { InnerHandler = pipeline };
        }
        return caller is null ? pipeline : new CallerHandler(caller) { InnerHandler = pipeline };
    }

    private static bool IsInternalNonLoopbackHost(string host)
    {
        if (string.IsNullOrEmpty(host))
        {
            return false;
        }

        if (string.Equals(host, "host.docker.internal", StringComparison.OrdinalIgnoreCase)
            || (host.Length > ".internal".Length
                && host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase)
                && Uri.CheckHostName(host) == UriHostNameType.Dns))
        {
            return true;
        }

        if (!IPAddress.TryParse(host, out var ip))
        {
            return false;
        }

        if (ip.IsIPv4MappedToIPv6)
        {
            ip = ip.MapToIPv4();
        }

        if (IPAddress.IsLoopback(ip))
        {
            return false;
        }

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            Span<byte> bytes = stackalloc byte[4];
            if (!ip.TryWriteBytes(bytes, out _))
            {
                return false;
            }

            return bytes[0] == 10
                || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
                || (bytes[0] == 192 && bytes[1] == 168)
                || (bytes[0] == 100 && bytes[1] is >= 64 and <= 127);
        }

        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            Span<byte> bytes = stackalloc byte[16];
            return ip.TryWriteBytes(bytes, out _) && (bytes[0] & 0xfe) == 0xfc;
        }

        return false;
    }

    internal static string Validated(string peerSecret) =>
        peerSecret.Length == 64 && peerSecret.All(char.IsAsciiHexDigit)
            ? peerSecret
            : throw new AuthConfigurationException($"{PeerSecretVariable} must contain 64 hexadecimal characters");

    private sealed class StrongConsistency : DelegatingHandler
    {
        private string? bookmark;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Headers.Remove(ConsistencyHeaders.Consistency);
            request.Headers.Add(ConsistencyHeaders.Consistency, ConsistencyHeaders.StrongValue);
            request.Headers.Remove(ConsistencyHeaders.Bookmark);
            if (Volatile.Read(ref bookmark) is { } after)
            {
                request.Headers.Add(ConsistencyHeaders.Bookmark, after);
            }

            var response = await base.SendAsync(request, cancellationToken);
            if (response.Headers.TryGetValues(ConsistencyHeaders.Bookmark, out var values) && values.LastOrDefault() is { Length: > 0 } latest)
            {
                Volatile.Write(ref bookmark, latest);
            }

            return response;
        }
    }

    private sealed class CallerHandler(CallerCredential caller) : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Headers.Remove(CallerCredential.HeaderName);
            request.Headers.Add(CallerCredential.HeaderName, caller.Token);
            return base.SendAsync(request, cancellationToken);
        }
    }

    private sealed class PeerCredentialHandler(string peerSecret) : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Headers.Remove(PeerCredentialHeader);
            request.Headers.Add(PeerCredentialHeader, peerSecret);
            return base.SendAsync(request, cancellationToken);
        }
    }

    private sealed class FailClosed(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            try
            {
                return await base.SendAsync(request, cancellationToken);
            }
            catch (HttpRequestException error)
            {
                throw ApiException.Unavailable(error);
            }
        }
    }

    private static async Task<IPEndPoint[]> EndPointsAsync(HostResolver resolve, DnsEndPoint host, CancellationToken cancellationToken)
    {
        try
        {
            return [.. (await resolve(host.Host, cancellationToken)).Select(address => new IPEndPoint(address, host.Port))];
        }
        catch (SocketException)
        {
            return [];
        }
    }

    private static async Task<Stream?> ConnectAsync(IPEndPoint endpoint, CancellationToken cancellationToken)
    {
        var socket = new Socket(endpoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        using var dial = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        dial.CancelAfter(DialTimeout);
        try
        {
            await socket.ConnectAsync(endpoint, dial.Token);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch (Exception error) when ((error is SocketException or OperationCanceledException) && !cancellationToken.IsCancellationRequested)
        {
            socket.Dispose();
            return null;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}

using System.Net;
using System.Net.Sockets;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Configuration;
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

    public static HttpMessageHandler CreateHandler(IReadOnlyList<DnsEndPoint> otherRegions, ILogger logger, HostResolver? resolve = null, string? peerSecret = null)
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
        handler.ConnectCallback = async (context, cancellationToken) =>
        {
            var tried = new HashSet<IPEndPoint>();
            foreach (var host in otherRegions.Prepend(context.DnsEndPoint))
            {
                foreach (var endpoint in await EndPointsAsync(resolve, host, cancellationToken).ConfigureAwait(false))
                {
                    if (tried.Add(endpoint) && await ConnectAsync(endpoint, cancellationToken).ConfigureAwait(false) is { } stream)
                    {
                        if (host != context.DnsEndPoint)
                        {
                            logger.ZLogWarning($"TerraBase in this region ({context.DnsEndPoint.Host:@Region}) did not answer; storage uses {endpoint:@Machine} ({host.Host:@Via}) until the connection is replaced");
                        }

                        return stream;
                    }
                }
            }

            logger.ZLogWarning($"No TerraBase machine answered at {context.DnsEndPoint.Host:@Region} or at any of {otherRegions.Count:@Fallbacks} fallbacks; storage fails closed");
            throw new HttpRequestException(HttpRequestError.ConnectionError, $"No TerraBase machine answered at {context.DnsEndPoint.Host}");
        };
        HttpMessageHandler pipeline = new FailClosed(handler);
        return peerSecret is null ? pipeline : new PeerCredentialHandler(peerSecret) { InnerHandler = pipeline };
    }

    internal static string Validated(string peerSecret) =>
        peerSecret.Length == 64 && peerSecret.All(char.IsAsciiHexDigit)
            ? peerSecret
            : throw new AuthConfigurationException($"{PeerSecretVariable} must contain 64 hexadecimal characters");

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
                return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
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
            return [.. (await resolve(host.Host, cancellationToken).ConfigureAwait(false)).Select(address => new IPEndPoint(address, host.Port))];
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
            await socket.ConnectAsync(endpoint, dial.Token).ConfigureAwait(false);
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

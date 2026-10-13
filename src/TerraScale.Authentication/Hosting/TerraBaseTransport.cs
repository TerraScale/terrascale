using System.Net;
using System.Net.Sockets;
using TerraScale.Authentication.Contracts;
using TerraScale.DotNet.TerraBase;
using ZLogger;

namespace TerraScale.Authentication.Hosting;

/// <summary>
/// The connections to TerraBase's internal listener. Each new connection goes to a machine of the request's host; when none
/// answers, to a machine of <c>otherRegions</c>, in order (none for the authority, which takes every write). Only where a
/// connection goes changes: a request is never resent, so a write is never applied twice. A TerraBase that cannot be
/// reached fails the operation closed: <see cref="ApiException.Unavailable"/> (503 <c>temporarily_unavailable</c>).
/// </summary>
public static class TerraBaseTransport
{
    // Same-region connects take milliseconds and cross-region ones a few hundred; two seconds allows one lost SYN.
    private static readonly TimeSpan DialTimeout = TimeSpan.FromSeconds(2);

    // Every connection is replaced after this long, so ts-auth is back in its own region that long after it answers again.
    private static readonly TimeSpan ConnectionLifetime = TimeSpan.FromSeconds(30);

    /// <summary>
    /// A handler that connects to the request's host, then to <paramref name="otherRegions"/>; each machine is tried once
    /// per connection.
    /// </summary>
    public static HttpMessageHandler CreateHandler(IReadOnlyList<DnsEndPoint> otherRegions, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(otherRegions);
        ArgumentNullException.ThrowIfNull(logger);
        var handler = TerraBaseHttp.CreateHandler();
        handler.PooledConnectionLifetime = ConnectionLifetime;
        // HTTP/2 pings close a connection to a machine that vanished (a host failure) instead of waiting for TCP to give up.
        handler.KeepAlivePingDelay = TimeSpan.FromSeconds(5);
        handler.KeepAlivePingTimeout = TimeSpan.FromSeconds(5);
        handler.ConnectCallback = async (context, cancellationToken) =>
        {
            var tried = new HashSet<IPEndPoint>();
            foreach (var host in otherRegions.Prepend(context.DnsEndPoint))
            {
                foreach (var endpoint in await EndPointsAsync(host, cancellationToken).ConfigureAwait(false))
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
        return new FailClosed(handler);
    }

    // Transport failures (no connection, a dropped one) answer 503 everywhere, never a 500 or a retry elsewhere.
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

    // Fly lists started machines only: a region without one does not resolve.
    private static async Task<IPEndPoint[]> EndPointsAsync(DnsEndPoint host, CancellationToken cancellationToken)
    {
        try
        {
            return [.. (await Dns.GetHostAddressesAsync(host.Host, cancellationToken).ConfigureAwait(false)).Select(address => new IPEndPoint(address, host.Port))];
        }
        catch (SocketException)
        {
            return [];
        }
    }

    // The connection, or null when the machine refuses it or does not answer in time.
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

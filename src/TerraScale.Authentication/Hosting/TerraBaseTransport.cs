using System.Net;
using System.Net.Sockets;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Configuration;
using TerraScale.Authentication.Features.RateLimiting;
using TerraScale.DotNet.TerraBase;
using ZLogger;

namespace TerraScale.Authentication.Hosting;

/// <summary>
/// The connections to TerraBase's internal listener. Each new connection goes to a machine of the request's host; when none
/// answers, to a machine of <c>otherRegions</c>, in order (none for the authority, which takes every write). Only where a
/// connection goes changes: a request is never resent, so a write is never applied twice. A TerraBase that cannot be
/// reached fails the operation closed: <see cref="ApiException.Unavailable"/> (503 <c>temporarily_unavailable</c>).
/// With a supplied peer credential (<c>TERRABASE_PEER_SECRET</c>), every request carries it. TerraBase verifies it on its internal listener.
/// </summary>
public static class TerraBaseTransport
{
    /// <summary>The environment variable holding the peer credential: 64 hexadecimal characters (32 random bytes).</summary>
    public const string PeerSecretVariable = "TERRABASE_PEER_SECRET";

    /// <summary>The request header that makes a TerraBase write wait for another region's durable acknowledgement.</summary>
    public const string DurabilityHeader = "TB-Durability";

    /// <summary>The request header carrying the peer credential on TerraBase's internal listener.</summary>
    public const string PeerCredentialHeader = "X-TerraBase-Peer-Credential";

    // Same-region connects take milliseconds and cross-region ones a few hundred; two seconds allows one lost SYN.
    private static readonly TimeSpan DialTimeout = TimeSpan.FromSeconds(2);

    // Regional fallback connections are replaced after this long, so ts-auth returns to its own region when it answers again.
    private static readonly TimeSpan ConnectionLifetime = TimeSpan.FromSeconds(30);

    /// <summary>The addresses of a host; system DNS unless the host registers another resolver (tests name loopback machines).</summary>
    public delegate Task<IPAddress[]> HostResolver(string host, CancellationToken cancellationToken);

    /// <summary>
    /// A handler that connects to the request's host, then to <paramref name="otherRegions"/>; each machine is tried once
    /// per connection. It sends <paramref name="peerSecret"/> when supplied; TerraBase records its verification outcome.
    /// </summary>
    public static HttpMessageHandler CreateHandler(IReadOnlyList<DnsEndPoint> otherRegions, ILogger logger, HostResolver? resolve = null, string? peerSecret = null)
    {
        resolve ??= Dns.GetHostAddressesAsync;
        ArgumentNullException.ThrowIfNull(otherRegions);
        ArgumentNullException.ThrowIfNull(logger);
        peerSecret = string.IsNullOrEmpty(peerSecret) ? null : Validated(peerSecret);
        var handler = TerraBaseHttp.CreateHandler();
        // Only a client with alternate endpoints needs periodic failback; the authority always stays at its named endpoint.
        if (otherRegions.Count > 0)
        {
            handler.PooledConnectionLifetime = ConnectionLifetime;
        }
        // HTTP/2 pings close a connection to a machine that vanished (a host failure) instead of waiting for TCP to give up.
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
        HttpMessageHandler pipeline = new RemoteDurableHandler { InnerHandler = new FailClosed(handler) };
        return peerSecret is null ? pipeline : new PeerCredentialHandler(peerSecret) { InnerHandler = pipeline };
    }

    internal static string Validated(string peerSecret) =>
        peerSecret.Length == 64 && peerSecret.All(char.IsAsciiHexDigit)
            ? peerSecret
            : throw new AuthConfigurationException($"{PeerSecretVariable} must contain 64 hexadecimal characters");

    // Credentials, sessions and revocations must survive the loss of one region: every write waits for another region's durable
    // ack (TerraBase decision 0013e makes that a per-request choice). Read routes ignore the header. Rate-limit buckets are the
    // exception: losing one with its region only reopens that window, while waiting for a remote ack on every attempt put a
    // cross-region round trip (and its conflict retries) on every token request (docs/auth/rate-limit-durability.md).
    private sealed class RemoteDurableHandler : DelegatingHandler
    {
        private static readonly string RateLimits = $"/collections/{AttemptLimiter.Collection}/";

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Headers.Remove(DurabilityHeader);
            if (request.RequestUri?.AbsolutePath.Contains(RateLimits, StringComparison.Ordinal) != true)
            {
                request.Headers.Add(DurabilityHeader, "remote_durable");
            }

            return base.SendAsync(request, cancellationToken);
        }
    }

    // Sets the credential on every request, so a request never leaves without it. Redirects are off in TerraBaseHttp, so it never
    // reaches another host.
    private sealed class PeerCredentialHandler(string peerSecret) : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Headers.Remove(PeerCredentialHeader);
            request.Headers.Add(PeerCredentialHeader, peerSecret);
            return base.SendAsync(request, cancellationToken);
        }
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

    // An unavailable DNS name supplies no connection candidates.
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

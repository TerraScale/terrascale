using System.Buffers;
using System.Globalization;
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

    private static readonly SearchValues<char> OriginCharacters =
        SearchValues.Create("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-.:[]/");

    private static readonly SearchValues<char> LabelCharacters =
        SearchValues.Create("abcdefghijklmnopqrstuvwxyz0123456789-");

    private static readonly SearchValues<char> HexCharacters =
        SearchValues.Create("0123456789abcdef");

    public delegate Task<IPAddress[]> HostResolver(string host, CancellationToken cancellationToken);

    public static Uri ClientOrigin(Uri origin)
    {
        ArgumentNullException.ThrowIfNull(origin);
        if (!origin.IsAbsoluteUri)
        {
            return origin;
        }

        var raw = origin.OriginalString;
        if (raw.Length > 2048 || raw.AsSpan().ContainsAnyExcept(OriginCharacters))
        {
            return origin;
        }

        var lower = raw.ToLowerInvariant();
        if (!lower.StartsWith("http://", StringComparison.Ordinal))
        {
            return origin;
        }

        var rest = lower["http://".Length..];
        rest = rest.EndsWith('/') ? rest[..^1] : rest;
        if (rest.Length == 0 || rest.Contains('/', StringComparison.Ordinal))
        {
            return origin;
        }

        var (host, after) = (rest, "");
        if (rest.StartsWith('['))
        {
            var end = rest.IndexOf(']', StringComparison.Ordinal);
            if (end < 0)
            {
                return origin;
            }

            (host, after) = (rest[..(end + 1)], rest[(end + 1)..]);
            if (!IsPrivateNonLoopbackIpv6(host[1..^1]))
            {
                return origin;
            }
        }
        else
        {
            if (rest.IndexOf(':', StringComparison.Ordinal) is var colon and >= 0)
            {
                (host, after) = (rest[..colon], rest[colon..]);
            }

            if (!IsInternalNonLoopbackNameOrIpv4(host))
            {
                return origin;
            }
        }

        var port = 80;
        if (after.Length > 0)
        {
            var portText = after[1..];
            if (after[0] != ':' || !Digits(portText) || portText.Length > 5 || portText[0] == '0'
                || !int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out port)
                || port > 65535)
            {
                return origin;
            }
        }

        if (origin.Port != port || !SameHost(origin, host))
        {
            return origin;
        }

        return port == 80
            ? new Uri("http://127.0.0.1/")
            : new Uri(string.Create(CultureInfo.InvariantCulture, $"http://127.0.0.1:{port}/"));
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

    private static bool Digits(string text) => text.Length > 0 && !text.AsSpan().ContainsAnyExceptInRange('0', '9');

    private static bool IsInternalNonLoopbackNameOrIpv4(string host)
    {
        if (host.Length == 0 || host.Length > 253)
        {
            return false;
        }

        var labels = host.Split('.');
        if (!labels.All(label => label.Length is >= 1 and <= 63
            && label[0] != '-'
            && label[^1] != '-'
            && !label.StartsWith("xn--", StringComparison.Ordinal)
            && !label.AsSpan().ContainsAnyExcept(LabelCharacters)))
        {
            return false;
        }

        var last = labels[^1];
        var numeric = Digits(last) || (last.StartsWith("0x", StringComparison.Ordinal) && !last.AsSpan(2).ContainsAnyExcept(HexCharacters));
        if (numeric)
        {
            if (labels.Length != 4)
            {
                return false;
            }

            Span<byte> octets = stackalloc byte[4];
            for (var i = 0; i < 4; i++)
            {
                var octet = labels[i];
                if (!Digits(octet) || octet.Length > 3 || (octet.Length > 1 && octet[0] == '0')
                    || !byte.TryParse(octet, NumberStyles.None, CultureInfo.InvariantCulture, out octets[i]))
                {
                    return false;
                }
            }

            return octets[0] == 10
                || (octets[0] == 172 && octets[1] is >= 16 and <= 31)
                || (octets[0] == 192 && octets[1] == 168)
                || (octets[0] == 100 && octets[1] is >= 64 and <= 127);
        }

        return host == "host.docker.internal" || (labels.Length >= 2 && last == "internal");
    }

    private static bool IsPrivateNonLoopbackIpv6(string text)
    {
        var halves = text.Split("::");
        if (text.Length == 0 || halves.Length > 2)
        {
            return false;
        }

        string[][] parts = [.. halves.Select(half => half.Length == 0 ? [] : half.Split(':'))];
        var count = parts.Sum(half => half.Length);
        if (parts.Length == 1 ? count != 8 : count > 7)
        {
            return false;
        }

        if (!parts.All(half => half.All(group => group.Length is >= 1 and <= 4 && !group.AsSpan().ContainsAnyExcept(HexCharacters))))
        {
            return false;
        }

        string[] groups = [.. parts[0], .. Enumerable.Repeat("0", 8 - count), .. parts.Length == 2 ? parts[1] : []];
        var first = ushort.Parse(groups[0], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
        return (first & 0xfe00) == 0xfc00;
    }

    private static bool SameHost(Uri origin, string host) =>
        host.StartsWith('[')
            ? IPAddress.TryParse(origin.DnsSafeHost, out var sent) && IPAddress.TryParse(host[1..^1], out var named) && sent.Equals(named)
            : string.Equals(origin.IdnHost, host, StringComparison.Ordinal);

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

using System.Buffers;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using TerraScale.Dashboard.Platform.Clients;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.Dashboard.Platform.Persistence;

public static class DashboardStorage
{
    public const string ProjectId = "00000000000000000000000005";
    public const string Database = "ts-dashboard";
    public const string StateHttpClientName = "dashboard-state";

    private static readonly TimeSpan DialTimeout = TimeSpan.FromSeconds(2);

    private static readonly SearchValues<char> OriginCharacters =
        SearchValues.Create("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-.:[]/");

    private static readonly SearchValues<char> LabelCharacters =
        SearchValues.Create("abcdefghijklmnopqrstuvwxyz0123456789-");

    private static readonly SearchValues<char> HexCharacters =
        SearchValues.Create("0123456789abcdef");

    extension(IServiceCollection services)
    {
        public IServiceCollection AddDashboardStorage()
        {
            services.AddSingleton(provider => ServiceUrls.From(provider.GetRequiredService<IHostEnvironment>(), Environment.GetEnvironmentVariable));
            services.AddSingleton<CausalBookmarks>();
            services.AddHttpClient(StateHttpClientName, (provider, client) => client.BaseAddress = ClientOrigin(provider.GetRequiredService<ServiceUrls>().TerraBase))
                .ConfigurePrimaryHttpMessageHandler(provider => CreateStateHandler(provider.GetRequiredService<ServiceUrls>().TerraBase))
                .AddHttpMessageHandler(provider => new TerraBaseHeaders(provider.GetRequiredService<IDashboardIdentity>(), provider.GetRequiredService<CausalBookmarks>(), true))
                .AddHttpMessageHandler(() => new StrongConsistency());
            services.AddHttpClient(TerraBaseHttpClient.HttpClientName, (provider, client) => client.BaseAddress = provider.GetRequiredService<ServiceUrls>().TerraBase)
                .ConfigurePrimaryHttpMessageHandler(TerraBaseHttp.CreateHandler)
                .AddHttpMessageHandler(provider => new TerraBaseHeaders(provider.GetRequiredService<IDashboardIdentity>(), provider.GetRequiredService<CausalBookmarks>(), false));
            services.AddHttpClient(PlatformHttpClient.HttpClientName, (provider, client) => client.BaseAddress = provider.GetRequiredService<ServiceUrls>().Platform)
                .ConfigurePrimaryHttpMessageHandler(TerraBaseHttp.CreateHandler)
                .AddHttpMessageHandler(provider => new PlatformHeaders(provider.GetRequiredService<IDashboardIdentity>(), provider.GetRequiredService<CausalBookmarks>()));
            services.AddHttpClient(ControlPlaneClient.HttpClientName, (provider, client) =>
                    client.BaseAddress = provider.GetRequiredService<ServiceUrls>() is { Control: { } control } ? control : provider.GetRequiredService<ServiceUrls>().TerraBase)
                .ConfigurePrimaryHttpMessageHandler(TerraBaseHttp.CreateHandler)
                .AddHttpMessageHandler(provider => new TerraBaseHeaders(provider.GetRequiredService<IDashboardIdentity>(), provider.GetRequiredService<CausalBookmarks>(), false));
            services.AddSingleton(provider => new TerraBaseHttpClient(Transport(provider, TerraBaseHttpClient.HttpClientName)));
            services.AddSingleton(provider => new PlatformHttpClient(Transport(provider, PlatformHttpClient.HttpClientName)));
            services.AddSingleton(provider => new ControlPlaneClient(Transport(provider, ControlPlaneClient.HttpClientName)));
            services.AddSingleton(provider => new DashboardDb(new TerraBaseSqlClient(provider.GetRequiredService<IHttpClientFactory>().CreateClient(StateHttpClientName),
                new TerraBaseSqlClientOptions { BaseAddress = ClientOrigin(provider.GetRequiredService<ServiceUrls>().TerraBase) }).Database(ProjectId, Database)));
            services.AddSingleton<BackgroundJobs>();
            services.AddHostedService<StorageSweeper>();
            return services;
        }
    }

    internal static Uri ClientOrigin(Uri target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (!target.IsAbsoluteUri)
        {
            return target;
        }

        var raw = target.OriginalString;
        if (raw.Length > 2048 || raw.AsSpan().ContainsAnyExcept(OriginCharacters))
        {
            return target;
        }

        var lower = raw.ToLowerInvariant();
        if (!lower.StartsWith("http://", StringComparison.Ordinal))
        {
            return target;
        }

        var rest = lower["http://".Length..];
        rest = rest.EndsWith('/') ? rest[..^1] : rest;
        if (rest.Length == 0 || rest.Contains('/', StringComparison.Ordinal))
        {
            return target;
        }

        var (host, after) = (rest, "");
        if (rest.StartsWith('['))
        {
            var end = rest.IndexOf(']', StringComparison.Ordinal);
            if (end < 0)
            {
                return target;
            }

            (host, after) = (rest[..(end + 1)], rest[(end + 1)..]);
            if (!IsPrivateNonLoopbackIpv6(host[1..^1]))
            {
                return target;
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
                return target;
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
                return target;
            }
        }

        if (target.Port != port || !SameHost(target, host))
        {
            return target;
        }

        return port == 80
            ? new Uri("http://127.0.0.1/")
            : new Uri(string.Create(CultureInfo.InvariantCulture, $"http://127.0.0.1:{port}/"));
    }

    private static SocketsHttpHandler CreateStateHandler(Uri target)
    {
        var handler = TerraBaseHttp.CreateHandler();
        if (ReferenceEquals(ClientOrigin(target), target))
        {
            return handler;
        }

        var destination = new DnsEndPoint(target.DnsSafeHost, target.Port);
        handler.ConnectCallback = (context, cancellationToken) =>
        {
            var endpoint = context.DnsEndPoint.Port == destination.Port
                && string.Equals(context.DnsEndPoint.Host, "127.0.0.1", StringComparison.Ordinal)
                ? destination
                : context.DnsEndPoint;
            return ConnectAsync(endpoint, cancellationToken);
        };
        return handler;
    }

    private static async ValueTask<Stream> ConnectAsync(DnsEndPoint endpoint, CancellationToken cancellationToken)
    {
        IPAddress[] addresses;
        if (IPAddress.TryParse(endpoint.Host, out var literal))
        {
            addresses = [literal];
        }
        else
        {
            try
            {
                addresses = await Dns.GetHostAddressesAsync(endpoint.Host, cancellationToken);
            }
            catch (SocketException error)
            {
                throw new HttpRequestException(HttpRequestError.NameResolutionError, $"Unable to resolve {endpoint.Host}.", error);
            }
        }

        if (addresses.Length == 0)
        {
            throw new HttpRequestException(HttpRequestError.NameResolutionError, $"No address resolved for {endpoint.Host}.");
        }

        Exception? last = null;
        for (var i = 0; i < addresses.Length; i++)
        {
            var address = addresses[i];
            var hasMore = i + 1 < addresses.Length;
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            using var attempt = hasMore ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken) : null;
            attempt?.CancelAfter(DialTimeout);
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, endpoint.Port), attempt?.Token ?? cancellationToken);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (SocketException error) when (!cancellationToken.IsCancellationRequested)
            {
                socket.Dispose();
                last = error;
            }
            catch (OperationCanceledException error) when (attempt is not null && !cancellationToken.IsCancellationRequested)
            {
                socket.Dispose();
                last = error;
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }

        throw new HttpRequestException(HttpRequestError.ConnectionError, $"Unable to connect to {endpoint.Host}:{endpoint.Port}.", last);
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

    private static ServiceTransport Transport(IServiceProvider provider, string name) =>
        new(provider.GetRequiredService<IHttpClientFactory>().CreateClient(name));
}

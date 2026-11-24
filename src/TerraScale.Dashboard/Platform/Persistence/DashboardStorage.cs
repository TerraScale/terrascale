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
        return target.Scheme == Uri.UriSchemeHttp
            && target.UserInfo.Length == 0
            && target.AbsolutePath == "/"
            && target.Query.Length == 0
            && target.Fragment.Length == 0
            && IsInternalNonLoopbackHost(target.DnsSafeHost)
            ? new UriBuilder(Uri.UriSchemeHttp, "127.0.0.1", target.Port).Uri
            : target;
    }

    private static SocketsHttpHandler CreateStateHandler(Uri target)
    {
        var handler = TerraBaseHttp.CreateHandler();
        if (ClientOrigin(target) == target)
        {
            return handler;
        }

        var destination = new DnsEndPoint(target.DnsSafeHost, target.Port);
        var connectTimeout = handler.ConnectTimeout;
        handler.ConnectCallback = (context, cancellationToken) =>
        {
            var endpoint = context.DnsEndPoint.Port == destination.Port
                && string.Equals(context.DnsEndPoint.Host, "127.0.0.1", StringComparison.Ordinal)
                ? destination
                : context.DnsEndPoint;
            return ConnectAsync(endpoint, connectTimeout, cancellationToken);
        };
        return handler;
    }

    private static async ValueTask<Stream> ConnectAsync(DnsEndPoint endpoint, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var dial = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (timeout != Timeout.InfiniteTimeSpan)
        {
            dial.CancelAfter(timeout);
        }

        IPAddress[] addresses;
        if (IPAddress.TryParse(endpoint.Host, out var literal))
        {
            addresses = [literal];
        }
        else
        {
            try
            {
                addresses = await Dns.GetHostAddressesAsync(endpoint.Host, dial.Token);
            }
            catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested && dial.IsCancellationRequested)
            {
                throw new TimeoutException($"Connecting to {endpoint.Host}:{endpoint.Port} timed out.", error);
            }
        }

        if (addresses.Length == 0)
        {
            throw new HttpRequestException(HttpRequestError.NameResolutionError, $"No address resolved for {endpoint.Host}.");
        }

        Exception? last = null;
        foreach (var address in addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, endpoint.Port), dial.Token);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception error) when (error is SocketException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
                socket.Dispose();
                if (dial.IsCancellationRequested)
                {
                    throw new TimeoutException($"Connecting to {endpoint.Host}:{endpoint.Port} timed out.", error);
                }

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

    private static ServiceTransport Transport(IServiceProvider provider, string name) =>
        new(provider.GetRequiredService<IHttpClientFactory>().CreateClient(name));
}

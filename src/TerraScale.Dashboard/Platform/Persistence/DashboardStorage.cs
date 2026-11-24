using System.Net;
using System.Net.Sockets;
using TerraScale.Contracts.Networking;
using TerraScale.Dashboard.Platform.Clients;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.Dashboard.Platform.Persistence;

public static class DashboardStorage
{
    public const string ProjectId = "00000000000000000000000005";
    public const string Database = "ts-dashboard";
    public const string StateHttpClientName = "dashboard-state";

    private static readonly TimeSpan DialTimeout = TimeSpan.FromSeconds(2);

    extension(IServiceCollection services)
    {
        public IServiceCollection AddDashboardStorage()
        {
            services.AddSingleton(provider => ServiceUrls.From(provider.GetRequiredService<IHostEnvironment>(), Environment.GetEnvironmentVariable));
            services.AddSingleton<CausalBookmarks>();
            services.AddHttpClient(StateHttpClientName, (provider, client) => client.BaseAddress = InternalTerraBaseOrigin.ClientOrigin(provider.GetRequiredService<ServiceUrls>().TerraBase))
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
                new TerraBaseSqlClientOptions { BaseAddress = InternalTerraBaseOrigin.ClientOrigin(provider.GetRequiredService<ServiceUrls>().TerraBase) }).Database(ProjectId, Database)));
            services.AddSingleton<BackgroundJobs>();
            services.AddHostedService<StorageSweeper>();
            return services;
        }
    }

    private static SocketsHttpHandler CreateStateHandler(Uri target)
    {
        var handler = TerraBaseHttp.CreateHandler();
        if (InternalTerraBaseOrigin.From(target) is { } internalOrigin)
        {
            handler.ConnectCallback = (context, cancellationToken) => ConnectAsync(internalOrigin.Dial(context.DnsEndPoint), cancellationToken);
        }

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
            Socket? socket = null;
            using var attempt = hasMore ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken) : null;
            attempt?.CancelAfter(DialTimeout);
            try
            {
                socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                socket.NoDelay = true;
                await socket.ConnectAsync(new IPEndPoint(address, endpoint.Port), attempt?.Token ?? cancellationToken);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (SocketException error) when (!cancellationToken.IsCancellationRequested)
            {
                socket?.Dispose();
                last = error;
            }
            catch (OperationCanceledException error) when (attempt is not null && !cancellationToken.IsCancellationRequested)
            {
                socket?.Dispose();
                last = error;
            }
            catch
            {
                socket?.Dispose();
                cancellationToken.ThrowIfCancellationRequested();
                throw;
            }
        }

        throw new HttpRequestException(HttpRequestError.ConnectionError, $"Unable to connect to {endpoint.Host}:{endpoint.Port}.", last);
    }

    private static ServiceTransport Transport(IServiceProvider provider, string name) =>
        new(provider.GetRequiredService<IHttpClientFactory>().CreateClient(name));
}

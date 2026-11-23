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
            services.AddHttpClient(StateHttpClientName, (provider, client) => client.BaseAddress = provider.GetRequiredService<ServiceUrls>().TerraBase)
                .ConfigurePrimaryHttpMessageHandler(TerraBaseHttp.CreateHandler)
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
                new TerraBaseSqlClientOptions { BaseAddress = provider.GetRequiredService<ServiceUrls>().TerraBase }).Database(ProjectId, Database)));
            services.AddSingleton<BackgroundJobs>();
            services.AddHostedService<StorageSweeper>();
            return services;
        }
    }

    private static ServiceTransport Transport(IServiceProvider provider, string name) =>
        new(provider.GetRequiredService<IHttpClientFactory>().CreateClient(name));
}

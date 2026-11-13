using System.Diagnostics.Metrics;
using TerraBase.Configuration;

namespace TerraBase.Features.Atlas;

public static class AtlasExtensions
{
    public static IServiceCollection AddTerraBaseAtlas(this IServiceCollection services, TerraBaseOptions options)
    {
        services.AddHttpClient(AtlasPlacementPublisher.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(15));
        if (options.AtlasUrl is null || options.AtlasPublishKeyFile is null)
        {
            services.AddSingleton<IAtlasPlacements>(NoAtlasPlacements.Instance);
            return services;
        }

        services.AddSingleton(provider => new AtlasPlacementPublisher(
            provider.GetRequiredService<IHttpClientFactory>(), options.AtlasPublishKey!, options.AtlasUrl,
            provider.GetRequiredService<TimeProvider>(), provider.GetRequiredService<IMeterFactory>(),
            provider.GetRequiredService<ILogger<AtlasPlacementPublisher>>(), provider.GetRequiredService<Management.SystemCatalog>(), options));
        services.AddSingleton<IAtlasPlacements>(provider => provider.GetRequiredService<AtlasPlacementPublisher>());
        services.AddHostedService(provider => provider.GetRequiredService<AtlasPlacementPublisher>());
        return services;
    }
}

using Microsoft.Extensions.DependencyInjection;

namespace TerraScale.DotNet;

/// <summary>
/// Dependency injection helpers for TerraScale SDK clients.
/// </summary>
public static class TerraScaleServiceCollectionExtensions
{
    /// <summary>
    /// Registers a singleton <see cref="TerraScaleClient"/> using <see cref="IHttpClientFactory"/>.
    /// </summary>
    public static IServiceCollection AddTerraScaleClient(
        this IServiceCollection services,
        Action<TerraScaleClientOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new TerraScaleClientOptions();
        configure(options);
        options.Validate();

        services.AddSingleton(options);
        services.AddHttpClient<TerraScaleClient>(httpClient =>
        {
            httpClient.BaseAddress = options.BaseAddress;
        });

        return services;
    }
}

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
        var optionSnapshot = options with { };
        optionSnapshot.Validate();

        services.AddSingleton(optionSnapshot);
        services.AddHttpClient<TerraScaleClient>(httpClient =>
        {
            httpClient.BaseAddress = optionSnapshot.BaseAddress;
        });

        return services;
    }
}

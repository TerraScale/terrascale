using Microsoft.Extensions.DependencyInjection;

namespace TerraScale.DotNet;

/// <summary>Dependency injection helpers for the ts-auth client.</summary>
public static class TerraScaleAuthServiceCollectionExtensions
{
    /// <summary>
    /// Registers a typed <see cref="TerraScaleAuthClient"/> for ts-auth management
    /// API operations using <see cref="IHttpClientFactory"/>.
    /// </summary>
    public static IServiceCollection AddTerraScaleAuthClient(
        this IServiceCollection services,
        Action<TerraScaleAuthClientOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new TerraScaleAuthClientOptions();
        configure(options);
        var optionSnapshot = options with { };
        optionSnapshot.Validate();

        services.AddSingleton(optionSnapshot);
        services.AddHttpClient<TerraScaleAuthClient>(httpClient =>
        {
            httpClient.BaseAddress = optionSnapshot.BaseAddress;
        }).ConfigurePrimaryHttpMessageHandler(static () => new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false });

        return services;
    }
}

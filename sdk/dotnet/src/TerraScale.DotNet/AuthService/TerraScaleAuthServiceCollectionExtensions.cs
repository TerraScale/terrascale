using Microsoft.Extensions.DependencyInjection;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.DotNet;

public static class TerraScaleAuthServiceCollectionExtensions
{
    public static IServiceCollection AddTerraScaleAuthClient(
        this IServiceCollection services,
        Action<TerraScaleAuthClientOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new TerraScaleAuthClientOptions();
        configure(options);
        var (address, slug) = options.Validate();
        var optionSnapshot = new TerraScaleAuthClientOptions { BaseAddress = address, InstanceSlug = slug };

        services.AddSingleton(optionSnapshot);
        services.AddHttpClient<TerraScaleAuthClient>(httpClient =>
        {
            httpClient.BaseAddress = address;
        }).ConfigurePrimaryHttpMessageHandler(static () => new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false, Proxy = new TerraBaseHttp.LoopbackDirect() });

        return services;
    }
}

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TerraScale.DotNet;

namespace TerraScale.EntityFrameworkCore;

/// <summary>
/// Dependency injection helpers for TerraScale Entity Framework Core source integration.
/// </summary>
public static class TerraScaleEntityFrameworkCoreServiceCollectionExtensions
{
    /// <summary>
    /// Registers TerraScale Entity Framework Core source services for a DbContext.
    /// </summary>
    public static IServiceCollection AddTerraScaleEntityFrameworkCore<TContext>(
        this IServiceCollection services,
        Action<TerraScaleEntityFrameworkCoreOptions> configure)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new TerraScaleEntityFrameworkCoreOptions();
        configure(options);
        var clientOptions = options.ToClientOptions();

        services.AddSingleton(options.Clone());
        services.AddTerraScaleClient(client =>
        {
            client.BaseAddress = clientOptions.BaseAddress;
            client.ApiKey = clientOptions.ApiKey;
            client.BearerToken = clientOptions.BearerToken;
            client.ProjectId = clientOptions.ProjectId;
        });
        services.AddScoped<TerraScaleEntitySource<TContext>>();

        return services;
    }
}

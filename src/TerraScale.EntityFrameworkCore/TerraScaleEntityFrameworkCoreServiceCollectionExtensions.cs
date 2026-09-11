using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TerraScale.DotNet;

namespace TerraScale.EntityFrameworkCore;
/// <summary>Dependency injection helpers for the EF Core integration.</summary>
public static class TerraScaleEntityFrameworkCoreServiceCollectionExtensions
{
    /// <summary>Registers the configured TerraScale client and source.</summary>
    public static IServiceCollection AddTerraScaleEntityFrameworkCore<TContext>(this IServiceCollection services, Action<TerraScaleEntityFrameworkCoreOptions> configure) where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(services); ArgumentNullException.ThrowIfNull(configure); var options = new TerraScaleEntityFrameworkCoreOptions(); configure(options); var snapshot = options.Clone();
        services.AddSingleton(snapshot); services.AddTerraScaleClient(client => { client.BaseAddress = snapshot.BaseAddress; client.ApiKey = snapshot.ApiKey; client.BearerToken = snapshot.BearerToken; client.BearerTokenProvider = snapshot.BearerTokenProvider; client.ProjectId = snapshot.ProjectId; }); services.AddScoped<TerraScaleEntitySource<TContext>>(); return services;
    }
}

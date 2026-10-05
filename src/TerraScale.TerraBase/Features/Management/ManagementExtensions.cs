using TerraScale.TerraBase.Features.Management.Backups;
using TerraScale.TerraBase.Features.Management.Indexes;
using TerraScale.TerraBase.Features.Management.Queries;
using TerraScale.TerraBase.Features.Management.Schema;
using TerraScale.TerraBase.Features.Management.Usage;
using TerraScale.TerraBase.Tenancy;

namespace TerraScale.TerraBase.Features.Management;

/// <summary>Registers the Management slice.</summary>
public static class ManagementExtensions
{
    /// <summary>
    /// Registers <see cref="IManagementService"/>, <see cref="IAccessService"/>, <see cref="ITenantDirectory"/> and the
    /// database tooling. Endpoints authenticate users with the <c>UserAuth</c> slice.
    /// </summary>
    public static IServiceCollection AddTerraBaseManagement(this IServiceCollection services)
    {
        // One catalog instance behind every interface: it remembers that the catalog version is current.
        services.AddSingleton<EngineManagementService>();
        services.AddHostedService(provider => provider.GetRequiredService<EngineManagementService>());
        services.AddSingleton<IManagementService>(provider => provider.GetRequiredService<EngineManagementService>());
        services.AddSingleton<IAccessService>(provider => provider.GetRequiredService<EngineManagementService>());
        services.AddSingleton<ITenantDirectory>(provider => provider.GetRequiredService<EngineManagementService>());
        services.AddSingleton<ICatalogGuard>(provider => provider.GetRequiredService<EngineManagementService>());
        services.AddSingleton<SystemCatalog>();
        return services
            .AddTerraBaseManagementBackups()
            .AddTerraBaseSchema()
            .AddTerraBaseUsage()
            .AddTerraBaseManagementQueries()
            .AddTerraBaseManagementIndexes();
    }
}

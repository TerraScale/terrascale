using TerraScale.TerraBase.Tenancy;

namespace TerraScale.TerraBase.Features.Management;

/// <summary>Registers the Management slice.</summary>
public static class ManagementExtensions
{
    /// <summary>
    /// Registers <see cref="IManagementService"/>, <see cref="IAccessService"/> and <see cref="ITenantDirectory"/>. Endpoints
    /// authenticate users with the <c>UserAuth</c> slice.
    /// </summary>
    public static IServiceCollection AddTerraBaseManagement(this IServiceCollection services)
    {
        // One catalog instance behind every interface: it remembers that the catalog version is current.
        services.AddSingleton<EngineManagementService>();
        services.AddHostedService(provider => provider.GetRequiredService<EngineManagementService>());
        services.AddSingleton<IManagementService>(provider => provider.GetRequiredService<EngineManagementService>());
        services.AddSingleton<IAccessService>(provider => provider.GetRequiredService<EngineManagementService>());
        services.AddSingleton<ITenantDirectory>(provider => provider.GetRequiredService<EngineManagementService>());
        return services;
    }
}

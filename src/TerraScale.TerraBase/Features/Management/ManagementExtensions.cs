using Microsoft.AspNetCore.Authentication;
using TerraScale.TerraBase.Tenancy;

namespace TerraScale.TerraBase.Features.Management;

/// <summary>Registers the Management slice.</summary>
public static class ManagementExtensions
{
    /// <summary>
    /// Registers <see cref="IManagementService"/>, <see cref="IAccessService"/>, <see cref="ITenantDirectory"/>, the
    /// <c>Dashboard</c> scheme and its policy.
    /// </summary>
    public static IServiceCollection AddTerraBaseManagement(this IServiceCollection services)
    {
        // One catalog instance behind every interface: it remembers that the catalog version is current.
        services.AddSingleton<EngineManagementService>();
        services.AddSingleton<IManagementService>(provider => provider.GetRequiredService<EngineManagementService>());
        services.AddSingleton<IAccessService>(provider => provider.GetRequiredService<EngineManagementService>());
        services.AddSingleton<ITenantDirectory>(provider => provider.GetRequiredService<EngineManagementService>());
        services.AddAuthentication()
            .AddScheme<AuthenticationSchemeOptions, DashboardAuthenticationHandler>(DashboardAuthenticationHandler.SchemeName, configureOptions: null);
        services.AddAuthorizationBuilder().AddPolicy(DashboardAuthenticationHandler.PolicyName, policy => policy
            .AddAuthenticationSchemes(DashboardAuthenticationHandler.SchemeName)
            .RequireAuthenticatedUser());
        return services;
    }
}

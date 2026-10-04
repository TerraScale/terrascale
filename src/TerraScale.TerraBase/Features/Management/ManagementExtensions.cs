using Microsoft.AspNetCore.Authentication;

namespace TerraScale.TerraBase.Features.Management;

/// <summary>Registers the Management slice.</summary>
public static class ManagementExtensions
{
    /// <summary>Registers <see cref="IManagementService"/>, <see cref="IAccessService"/>, the <c>Dashboard</c> scheme and its policy.</summary>
    public static IServiceCollection AddTerraBaseManagement(this IServiceCollection services)
    {
        services.AddSingleton<EngineManagementService>();
        services.AddSingleton<IManagementService>(provider => provider.GetRequiredService<EngineManagementService>());
        services.AddSingleton<IAccessService>(provider => provider.GetRequiredService<EngineManagementService>());
        services.AddAuthentication()
            .AddScheme<AuthenticationSchemeOptions, DashboardAuthenticationHandler>(DashboardAuthenticationHandler.SchemeName, configureOptions: null);
        services.AddAuthorizationBuilder().AddPolicy(DashboardAuthenticationHandler.PolicyName, policy => policy
            .AddAuthenticationSchemes(DashboardAuthenticationHandler.SchemeName)
            .RequireAuthenticatedUser());
        return services;
    }
}

namespace TerraScale.TerraBase.Features.Management;

/// <summary>Registers the Management slice.</summary>
public static class ManagementExtensions
{
    /// <summary>Registers <see cref="IManagementService"/>.</summary>
    public static IServiceCollection AddTerraBaseManagement(this IServiceCollection services) =>
        services.AddSingleton<IManagementService, EngineManagementService>();
}

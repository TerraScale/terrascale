using TerraBase.Features.Backup;
using TerraBase.Features.Catalog;
using TerraBase.Features.Management.Backups;
using TerraBase.Features.Management.Usage;
using TerraBase.Features.Qos;
using TerraBase.Tenancy;

namespace TerraBase.Features.Management;

internal static class ManagementExtensions
{
    public static IServiceCollection AddTerraBaseManagement(this IServiceCollection services)
    {
        services.AddSingleton<EngineManagementService>();
        services.AddSingleton<DatabaseAllowance>();
        services.AddSingleton(provider => new Lazy<ICatalogGate>(provider.GetRequiredService<ICatalogGate>));
        services.AddSingleton(provider => new Lazy<IStorageQuota>(provider.GetRequiredService<IStorageQuota>));
        services.AddSingleton(provider => new Lazy<IBackupKeyShredding>(provider.GetRequiredService<IBackupKeyShredding>));
        services.AddHostedService(provider => provider.GetRequiredService<EngineManagementService>());
        services.AddHostedService<CatalogMaintenance>();
        services.AddSingleton<IManagementService>(provider => provider.GetRequiredService<EngineManagementService>());
        services.AddSingleton<IProjectCatalog>(provider => provider.GetRequiredService<EngineManagementService>());
        services.AddSingleton<ITenantDirectory>(provider => provider.GetRequiredService<EngineManagementService>());
        services.AddSingleton<IHomeMoveCatalog>(provider => provider.GetRequiredService<EngineManagementService>());
        services.AddSingleton<Platform.IPlatformCatalog>(provider => provider.GetRequiredService<EngineManagementService>());
        services.AddSingleton<ICatalogGuard>(provider => provider.GetRequiredService<EngineManagementService>());
        services.AddSingleton<IDatabaseFileCatalog>(provider => provider.GetRequiredService<EngineManagementService>());
        services.AddSingleton<SystemCatalog>();
        services.AddSingleton<PlanFanout>();
        services.AddSingleton<EnrollmentFanout>();
        services.AddSingleton<RemoteDatabases>();
        services.AddHttpClient(RemoteDatabases.HttpClientName);
        return services
            .AddTerraBaseManagementBackups()
            .AddTerraBaseUsage();
    }
}

using TerraBase.Features.Management.Backups;
using TerraBase.Features.Management.Indexes;
using TerraBase.Features.Management.Queries;
using TerraBase.Features.Management.Schema;
using TerraBase.Features.Management.Usage;
using TerraBase.Features.Qos;
using TerraBase.Features.Replication;
using TerraBase.Features.Webhooks;
using TerraBase.Tenancy;

namespace TerraBase.Features.Management;

/// <summary>Registers the Management slice.</summary>
public static class ManagementExtensions
{
    /// <summary>
    /// <see cref="ITenantDirectory"/> and the database tooling. Endpoints authenticate users with the <c>UserAuth</c> slice.
    /// </summary>
    public static IServiceCollection AddTerraBaseManagement(this IServiceCollection services)
    {
        // One catalog instance behind every interface: it remembers that the catalog version is current.
        services.AddSingleton<EngineManagementService>();
        services.AddSingleton<DatabaseAllowance>();
        services.AddSingleton(provider => new Lazy<ICatalogGate>(provider.GetRequiredService<ICatalogGate>));
        services.AddSingleton(provider => new Lazy<IStorageQuota>(provider.GetRequiredService<IStorageQuota>));
        services.AddSingleton(provider => new Lazy<IRemoteDurability>(provider.GetRequiredService<IRemoteDurability>));
        services.AddSingleton(provider => new Lazy<IWebhookErasure>(provider.GetRequiredService<IWebhookErasure>));
        services.AddSingleton(provider => new Lazy<IEscrowPeers>(provider.GetRequiredService<IEscrowPeers>));
        services.AddSingleton<IDatabaseEscrow>(provider => provider.GetRequiredService<EngineManagementService>());
        services.AddHostedService(provider => provider.GetRequiredService<EngineManagementService>());
        services.AddHostedService<CatalogConvergence>();
        services.AddSingleton<IManagementService>(provider => provider.GetRequiredService<EngineManagementService>());
        services.AddSingleton<IProjectCatalog>(provider => provider.GetRequiredService<EngineManagementService>());
        services.AddSingleton<ITenantDirectory>(provider => provider.GetRequiredService<EngineManagementService>());
        services.AddSingleton<Platform.IPlatformCatalog>(provider => provider.GetRequiredService<EngineManagementService>());
        services.AddSingleton<ICatalogGuard>(provider => provider.GetRequiredService<EngineManagementService>());
        services.AddSingleton<IDatabaseFileCatalog>(provider => provider.GetRequiredService<EngineManagementService>());
        services.AddSingleton<SystemCatalog>();
        services.AddSingleton<ClosureCancellation>();
        services.AddSingleton<IClosureCancellation>(provider => provider.GetRequiredService<ClosureCancellation>());
        services.AddSingleton<HistoryErasures>();
        services.AddHostedService(provider => provider.GetRequiredService<HistoryErasures>());
        return services
            .AddTerraBaseManagementBackups()
            .AddTerraBaseSchema()
            .AddTerraBaseUsage()
            .AddTerraBaseManagementQueries()
            .AddTerraBaseManagementIndexes();
    }
}

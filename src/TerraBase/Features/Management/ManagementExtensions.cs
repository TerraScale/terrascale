using TerraBase.Features.Documents;
using TerraBase.Features.Management.Backups;
using TerraBase.Features.Management.Indexes;
using TerraBase.Features.Management.Queries;
using TerraBase.Features.Management.Schema;
using TerraBase.Features.Management.Usage;
using TerraBase.Features.Qos;
using TerraBase.Features.Webhooks;
using TerraBase.Tenancy;

namespace TerraBase.Features.Management;

public static class ManagementExtensions
{
    public static IServiceCollection AddTerraBaseManagement(this IServiceCollection services)
    {
        services.AddSingleton<EngineManagementService>();
        services.AddSingleton<DatabaseAllowance>();
        services.AddSingleton(provider => new Lazy<IDocumentService>(provider.GetRequiredService<IDocumentService>));
        services.AddSingleton(provider => new Lazy<IQueryShapeTracker>(provider.GetRequiredService<IQueryShapeTracker>));
        services.AddSingleton(provider => new Lazy<IStorageQuota>(provider.GetRequiredService<IStorageQuota>));
        services.AddSingleton(provider => new Lazy<IWebhookErasure>(provider.GetRequiredService<IWebhookErasure>));
        services.AddSingleton(provider => new Lazy<IEscrowPeers>(provider.GetRequiredService<IEscrowPeers>));
        services.AddSingleton<IDatabaseEscrow>(provider => provider.GetRequiredService<EngineManagementService>());
        services.AddHostedService(provider => provider.GetRequiredService<EngineManagementService>());
        services.AddHostedService<CatalogConvergence>();
        services.AddSingleton<IManagementService>(provider => provider.GetRequiredService<EngineManagementService>());
        services.AddSingleton<IProjectCatalog>(provider => provider.GetRequiredService<EngineManagementService>());
        services.AddSingleton<IAccessService>(provider => provider.GetRequiredService<EngineManagementService>());
        services.AddSingleton<IManagementCommits>(provider => provider.GetRequiredService<EngineManagementService>());
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

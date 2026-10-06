using TerraScale.TerraBase.Features.Documents;
using TerraScale.TerraBase.Features.Management.Backups;
using TerraScale.TerraBase.Features.Management.Indexes;
using TerraScale.TerraBase.Features.Management.Queries;
using TerraScale.TerraBase.Features.Management.Schema;
using TerraScale.TerraBase.Features.Management.Usage;
using TerraScale.TerraBase.Features.Replication;
using TerraScale.TerraBase.Tenancy;

namespace TerraScale.TerraBase.Features.Management;

/// <summary>Registers the Management slice.</summary>
public static class ManagementExtensions
{
    /// <summary>
    /// Registers <see cref="IManagementService"/>, <see cref="IAccessService"/>, <see cref="IManagementCommits"/>,
    /// <see cref="ITenantDirectory"/> and the database tooling. Endpoints authenticate users with the <c>UserAuth</c> slice.
    /// </summary>
    public static IServiceCollection AddTerraBaseManagement(this IServiceCollection services)
    {
        // One catalog instance behind every interface: it remembers that the catalog version is current.
        services.AddSingleton<EngineManagementService>();
        // Commits apply document operations; the document service reaches the catalog back through usage metering.
        services.AddSingleton(provider => new Lazy<IDocumentService>(provider.GetRequiredService<IDocumentService>));
        services.AddHostedService(provider => provider.GetRequiredService<EngineManagementService>());
        services.AddSingleton<IManagementService>(provider => provider.GetRequiredService<EngineManagementService>());
        services.AddSingleton<IAccessService>(provider => provider.GetRequiredService<EngineManagementService>());
        services.AddSingleton<IManagementCommits>(provider => provider.GetRequiredService<EngineManagementService>());
        services.AddSingleton<ITenantDirectory>(provider => provider.GetRequiredService<EngineManagementService>());
        services.AddSingleton<ICatalogGuard>(provider => provider.GetRequiredService<EngineManagementService>());
        services.AddSingleton<ICatalogApplyObserver>(provider => provider.GetRequiredService<EngineManagementService>());
        services.AddSingleton<SystemCatalog>();
        return services
            .AddTerraBaseManagementBackups()
            .AddTerraBaseSchema()
            .AddTerraBaseUsage()
            .AddTerraBaseManagementQueries()
            .AddTerraBaseManagementIndexes();
    }
}

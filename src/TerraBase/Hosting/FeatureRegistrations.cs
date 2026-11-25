using TerraBase.Features.ApiKeys;
using TerraBase.Features.Atlas;
using TerraBase.Features.Backup;
using TerraBase.Features.Callers;
using TerraBase.Features.Catalog;
using TerraBase.Features.ControlPlane;
using TerraBase.Features.Health;
using TerraBase.Features.Management;
using TerraBase.Features.Management.Logs;
using TerraBase.Features.Management.Usage;
using TerraBase.Features.Platform;
using TerraBase.Features.PostgresHost;
using TerraBase.Features.Pressure;
using TerraBase.Features.Qos;
using TerraBase.Features.Regions;
using TerraBase.Features.Rows;
using TerraBase.Features.Rules;
using TerraBase.Features.Sql;
using TerraBase.Features.UserAuth;
using TerraBase.Postgres.Hosting;
using TerraScale.QuicRpc;

namespace TerraBase.Hosting;

internal static class FeatureRegistrations
{
    extension(IServiceCollection services)
    {
        public IServiceCollection AddTerraBaseFeatures()
        {
            services.AddSingleton<CallerTokens>();
            services.AddSingleton<CallerAdmission>();
            services.AddSingleton<CallerAuthentication>();
            services.AddSingleton<AuthenticationGate>();
            services.AddSingleton<TokenVerifier>();
            services.AddSingleton<ApiKeyExchange>();
            services.AddSingleton<ControlSnapshots>();
            services.AddSingleton<ControlSnapshotSync>();
            services.AddSingleton<CatalogStore>();
            services.AddSingleton<DatabaseAccess>();
            services.AddSingleton<PlatformCatalog>();
            services.AddSingleton<ApiKeyStore>();
            services.AddSingleton<RegionDirectory>();
            services.AddSingleton<RulesStore>();
            services.AddSingleton<RulesEnforcer>();
            services.AddSingleton<RulesService>();
            services.AddSingleton<QosAdmission>();
            services.AddSingleton<TenantAdmission>();
            services.AddSingleton<TenantSessions>();
            services.AddSingleton<TenantDirectory>();
            services.AddSingleton<PostgresTenancy>();
            services.AddSingleton<SqlRunner>();
            services.AddSingleton<SqlFlow>();
            services.AddSingleton<SqlSockets>();
            services.AddSingleton<RowsService>();
            services.AddSingleton<ManagementService>();
            services.AddSingleton<BranchFlow>();
            services.AddSingleton<BackupFlow>();
            services.AddSingleton<UsageReports>();
            services.AddSingleton<NodeReadiness>();
            services.AddSingleton<PressureSignals>();
            services.AddSingleton<AtlasPublisher>();
            services.AddHostedService(static provider => provider.GetRequiredService<AtlasPublisher>());
            services.AddSingleton<UsageMeter>();
            services.AddHostedService(static provider => provider.GetRequiredService<UsageMeter>());
            services.AddSingleton<StorageOwnership>();
            services.AddSingleton<StorageLedger>();
            services.AddSingleton<StorageFiles>();
            services.AddSingleton<StorageMeasure>();
            services.AddSingleton<IQuicEndpoints, StorageFileProbeQuicEndpoint>();
            services.AddSingleton<UsageCommits>();
            services.AddSingleton<StorageSampler>();
            services.AddHostedService(static provider => provider.GetRequiredService<StorageSampler>());
            services.AddSingleton<RequestLogRecorder>();
            services.AddHostedService(static provider => provider.GetRequiredService<RequestLogRecorder>());
            services.AddHostedService<DatabaseExpiry>();
            services.AddSingleton<IPostgresSessionFactory, PostgresSessionFactory>();
            return services;
        }
    }
}

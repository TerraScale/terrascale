using TerraBase.Configuration;
using TerraBase.Errors;
using TerraBase.Features.Clock;
using TerraBase.Hosting;
using TerraBase.Tenancy;

namespace TerraBase.Features.Backup;

public static class BackupExtensions
{
    public static IServiceCollection AddTerraBaseBackup(this IServiceCollection services, TerraBaseOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Backup is not { } settings)
        {
            return services.AddSingleton<NoBackup>()
                .AddSingleton<IContinuousBackup>(provider => provider.GetRequiredService<NoBackup>())
                .AddSingleton<IPointInTimeRestore>(provider => provider.GetRequiredService<NoBackup>())
                .AddSingleton<IBackupKeyShredding>(provider => provider.GetRequiredService<NoBackup>());
        }

        services.AddHttpClient(S3ObjectStore.HttpClientName);
        services.AddSingleton<ICustomerBackupDns, CustomerBackupDns>();
        services.AddSingleton<ICustomerBackupConnector, CustomerBackupConnector>();
        services.AddHttpClient(S3ObjectStore.CustomerHttpClientName)
            .ConfigurePrimaryHttpMessageHandler(provider => CustomerBackupNetwork.Handler(
                provider.GetRequiredService<ICustomerBackupDns>(), provider.GetRequiredService<ICustomerBackupConnector>()))
            .AddHttpMessageHandler<CustomerBackupResponseLimits>();
        services.AddTransient<CustomerBackupResponseLimits>();
        var master = new MasterKey(settings.MasterKey);
        services.AddGrpc().AddServiceOptions<KeyRevocationService>(grpc =>
        {
            grpc.CompressionProviders = [new ClockCompression()];
            grpc.ResponseCompressionAlgorithm = "lz4";
            grpc.MaxReceiveMessageSize = 4096;
            grpc.MaxSendMessageSize = 4096;
        });
        return services
            .AddSingleton(settings)
            .AddSingleton<S3ObjectStore>()
            .AddSingleton<IObjectStore>(provider => provider.GetRequiredService<S3ObjectStore>())
            .AddSingleton<IMasterKey>(master)
            .AddSingleton<KeyCustody>()
            .AddSingleton<KeyRevocationLocal>()
            .AddSingleton<KeyRevocationPeers>()
            .AddSingleton(provider => new Lazy<Management.EngineManagementService>(provider.GetRequiredService<Management.EngineManagementService>))
            .AddSingleton<ShreddedDatabaseSweep>()
            .AddHostedService(provider => provider.GetRequiredService<ShreddedDatabaseSweep>())
            .AddSingleton(provider => new DataKeys(provider.GetRequiredService<KeyCustody>().Store, provider.GetRequiredService<IMasterKey>(),
                provider.GetRequiredService<TimeProvider>(), provider.GetRequiredService<BackupSettings>(),
                revocations: provider.GetRequiredService<KeyCustody>().Revocations))
            .AddHostedService<DataKeyRefresh>()
            .AddSingleton<BackupTargets>()
            .AddSingleton<ContinuousBackup>()
            .AddSingleton<IContinuousBackup>(provider => provider.GetRequiredService<ContinuousBackup>())
            .AddHostedService(provider => provider.GetRequiredService<ContinuousBackup>())
            .AddSingleton<DatabaseBackupPurge>()
            .AddHostedService(provider => provider.GetRequiredService<DatabaseBackupPurge>())
            .AddSingleton<IBackupKeyShredding>(provider => provider.GetRequiredService<DatabaseBackupPurge>())
            .AddSingleton<BackupReader>()
            .AddSingleton<IPointInTimeRestore, PointInTimeRestore>();
    }

    internal static void MapTerraBaseKeyRevocation(this WebApplication app, TerraBaseOptions options)
    {
        if (options.Backup is not null)
        {
            app.MapGrpcService<KeyRevocationService>().WithMetadata(ListenerMetadata.Internal);
        }
    }

    private sealed class NoBackup : IContinuousBackup, IPointInTimeRestore, IBackupKeyShredding
    {
        public Task<bool> DestroyAsync(TenantContext database, CancellationToken cancellationToken) => Task.FromResult(true);

        public void Shred(TenantContext database)
        {
        }

        public BackupStatus Status { get; } = new(false, []);

        public DateTimeOffset? LastShipped(TenantContext database, Ulid? generation = null) => null;

        public Task RunOnceAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<RestoreWindowResult> GetWindowAsync(TenantContext database, CancellationToken cancellationToken, string destination = BackupDestinationNames.Platform) => Task.FromResult<RestoreWindowResult>(new NotFound());

        public Task<PointInTimePlanResult> PlanAsync(TenantContext database, DateTimeOffset at, CancellationToken cancellationToken, string destination = BackupDestinationNames.Platform) =>
            Task.FromResult<PointInTimePlanResult>(new NotFound("continuous backup holds nothing of this database"));

        public Task<HistoricalSnapshotResult> ReadSnapshotAsync(TenantContext source, DateTimeOffset at, CancellationToken cancellationToken, string destination = BackupDestinationNames.Platform) =>
            Task.FromResult<HistoricalSnapshotResult>(new NotFound("continuous backup holds nothing of this database"));

        public Task<PointInTimeRestoreResult> RestoreAsync(TenantContext source, DateTimeOffset at, TenantContext target, CancellationToken cancellationToken, string destination = BackupDestinationNames.Platform) =>
            Task.FromResult<PointInTimeRestoreResult>(new NotFound("continuous backup holds nothing of this database"));
    }
}

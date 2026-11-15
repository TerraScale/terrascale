using System.Diagnostics.Metrics;
using TerraBase.Configuration;
using TerraBase.Errors;
using TerraBase.Features.Clock;
using TerraBase.Hosting;
using TerraBase.Tenancy;
using ZLogger;

namespace TerraBase.Features.Backup;

public static class BackupExtensions
{
    public static IServiceCollection AddTerraBaseBackup(this IServiceCollection services, TerraBaseOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Backup is not { } settings)
        {
            return services.AddSingleton<NoBackup>()
                .AddHostedService(provider => provider.GetRequiredService<NoBackup>())
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
            .AddSingleton<KeyCustody>()
            .AddSingleton<KeyRevocationLocal>()
            .AddSingleton<KeyRevocationPeers>()
            .AddSingleton<IShreddedDatabases>(provider => provider.GetRequiredService<Management.EngineManagementService>())
            .AddSingleton<ShreddedDatabaseSweep>()
            .AddHostedService(provider => provider.GetRequiredService<ShreddedDatabaseSweep>())
            .AddSingleton(provider => new DataKeys(provider.GetRequiredService<KeyCustody>().Store, provider.GetRequiredService<IKeyring>(),
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

    private sealed class NoBackup(IMeterFactory meters, ILogger<NoBackup> logger) : IContinuousBackup, IPointInTimeRestore, IBackupKeyShredding, IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken)
        {
            var meter = meters.Create(Telemetry.MeterName);
            meter.CreateObservableGauge("terrabase.backup.disabled", () => 1,
                description: "Continuous backup is disabled on this node; no new recovery points are stored.");
            PublishBackupSeries(meter);
            meter.CreateObservableGauge("terrabase.backup.holes", () => 0,
                description: "Holes recorded in the durable backup accounts of the files this node has loaded.");
            logger.ZLogCritical($"Continuous backup is disabled on this node; no new recovery points are stored");
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        private static void PublishBackupSeries(Meter meter)
        {
            CounterSeries.Zero(meter.CreateCounter<long>("terrabase.backup.uploaded", "By", "Sealed backup bytes stored in the bucket, by kind (snapshot, segment)."), "kind", "snapshot", "segment", "lineage", "hole");
            CounterSeries.Zero(meter.CreateCounter<long>("terrabase.backup.failures", description: "Backup passes of a file that failed."));
            CounterSeries.Zero(meter.CreateCounter<long>("terrabase.backup.holes_recorded", description: "Holes written to a file's durable backup account on this node: history the engine no longer retains was never stored."));
            CounterSeries.Zero(meter.CreateCounter<long>("terrabase.backup.generation_restarts", description: "Generations started without a hole, by reason (lineage: the file or its destination changed; key_lost: the generation's data key is gone; log_unplaceable: the log cannot express a schema change)."), "reason", "lineage", "key_lost", "log_unplaceable");
            CounterSeries.Zero(meter.CreateCounter<long>("terrabase.backup.snapshots_deferred", description: "Passes that put off a due daily snapshot, by reason (backlog: unread log remains; busy: snapshot uploads in flight)."), "reason", "backlog", "busy");
            CounterSeries.Zero(meter.CreateCounter<long>("terrabase.backup.generations", description: "Generations started (each stores a snapshot)."));
            CounterSeries.Zero(meter.CreateCounter<long>("terrabase.backup.drills", description: "Restore drills, by result (passed, failed)."), "result", "passed", "failed");
            CounterSeries.Zero(meter.CreateCounter<long>("terrabase.backup.bucket_snapshots", description: "Tigris snapshots of the backup bucket, by result (taken, deleted, failed)."), "result", "taken", "deleted", "failed");
        }

        public Task<bool> DestroyAsync(TenantContext database, DateTimeOffset until, CancellationToken cancellationToken) => Task.FromResult(true);

        public void Shred(TenantContext database)
        {
        }

        public BackupStatus Status { get; } = new(false, []);

        public bool Healthy => false;

        public Task<BackupHoles> HolesAsync(TenantContext database, CancellationToken cancellationToken) => Task.FromResult(new BackupHoles([], null));

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

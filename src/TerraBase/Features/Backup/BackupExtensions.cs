using TerraBase.Configuration;
using TerraBase.Errors;
using TerraBase.Tenancy;

namespace TerraBase.Features.Backup;

/// <summary>Registers continuous backup (TS-633).</summary>
public static class BackupExtensions
{
    /// <summary>
    /// Registers <see cref="IContinuousBackup"/> and <see cref="IPointInTimeRestore"/>: shipping to and restoring from the
    /// bucket of <see cref="TerraBaseOptions.Backup"/>, or nothing when backup is off.
    /// </summary>
    public static IServiceCollection AddTerraBaseBackup(this IServiceCollection services, TerraBaseOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Backup is not { } settings)
        {
            return services.AddSingleton<NoBackup>()
                .AddSingleton<IContinuousBackup>(provider => provider.GetRequiredService<NoBackup>())
                .AddSingleton<IPointInTimeRestore>(provider => provider.GetRequiredService<NoBackup>());
        }

        // Engine/Cluster registers IShipperLeases: a database's primary member ships it while it holds its lease.
        services.AddHttpClient(S3ObjectStore.HttpClientName);
        services.AddSingleton<ICustomerBackupDns, CustomerBackupDns>();
        services.AddSingleton<ICustomerBackupConnector, CustomerBackupConnector>();
        services.AddHttpClient(S3ObjectStore.CustomerHttpClientName)
            .ConfigurePrimaryHttpMessageHandler(provider => CustomerBackupNetwork.Handler(
                provider.GetRequiredService<ICustomerBackupDns>(), provider.GetRequiredService<ICustomerBackupConnector>()))
            .AddHttpMessageHandler<CustomerBackupResponseLimits>();
        services.AddTransient<CustomerBackupResponseLimits>();
        var master = new MasterKey(settings.MasterKey);
        return services
            .AddSingleton(settings)
            .AddSingleton<S3ObjectStore>()
            .AddSingleton<IObjectStore>(provider => provider.GetRequiredService<S3ObjectStore>())
            .AddSingleton<IMasterKey>(master)
            .AddSingleton<DataKeys>()
            .AddSingleton<BackupTargets>()
            .AddSingleton<ContinuousBackup>()
            .AddSingleton<IContinuousBackup>(provider => provider.GetRequiredService<ContinuousBackup>())
            .AddHostedService(provider => provider.GetRequiredService<ContinuousBackup>())
            .AddSingleton<DatabaseBackupPurge>()
            .AddHostedService(provider => provider.GetRequiredService<DatabaseBackupPurge>())
            .AddSingleton<BackupReader>()
            .AddSingleton<IPointInTimeRestore, PointInTimeRestore>();
    }

    // Backup is off: nothing ships and nothing can be restored.
    private sealed class NoBackup : IContinuousBackup, IPointInTimeRestore
    {
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

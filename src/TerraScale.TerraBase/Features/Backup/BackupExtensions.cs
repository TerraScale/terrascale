using TerraBase.Configuration;
using TerraBase.Engine;
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

        // anna-full (TS-647): the database primary member lease of Engine/Cluster replaces both interim choices.
        if (options.Nats is { } nats)
        {
            services.AddSingleton(nats).AddSingleton<ILeaseStore, NatsLeaseStore>().AddSingleton<IShipperLeases, LeasedShippers>();
        }
        else
        {
            services.AddSingleton<IShipperLeases, SingleShipper>();
        }

        services.AddHttpClient(S3ObjectStore.HttpClientName);
        services.AddHttpClient(OciVaultMasterKey.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(30));
        return services
            .AddSingleton(settings)
            .AddSingleton<IObjectStore, S3ObjectStore>()
            .AddSingleton<IMasterKey, OciVaultMasterKey>()
            .AddSingleton<DataKeys>()
            .AddSingleton<ContinuousBackup>()
            .AddSingleton<IContinuousBackup>(provider => provider.GetRequiredService<ContinuousBackup>())
            .AddHostedService(provider => provider.GetRequiredService<ContinuousBackup>())
            .AddSingleton<IPointInTimeRestore, PointInTimeRestore>();
    }

    // Backup is off: nothing ships and nothing can be restored.
    private sealed class NoBackup : IContinuousBackup, IPointInTimeRestore
    {
        public BackupStatus Status { get; } = new(false, []);

        public DateTimeOffset? LastShipped(TenantContext database) => null;

        public Task RunOnceAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<RestoreWindow?> GetWindowAsync(TenantContext database, CancellationToken cancellationToken) => Task.FromResult<RestoreWindow?>(null);

        public Task<bool> RestoreAsync(TenantContext source, DateTimeOffset at, TenantContext target, CancellationToken cancellationToken) =>
            Task.FromResult(false);
    }
}

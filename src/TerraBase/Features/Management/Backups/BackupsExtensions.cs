namespace TerraBase.Features.Management.Backups;

/// <summary>Registers database backups.</summary>
public static class BackupsExtensions
{
    /// <summary>Registers <see cref="IManagedBackupService"/> the backup scheduler and scheduled exports.</summary>
    public static IServiceCollection AddTerraBaseManagementBackups(this IServiceCollection services) =>
        services
            .AddSingleton<ISelectiveRecovery, SelectiveRecovery>()
            .AddSingleton<IManagedBackupService, EngineManagedBackupService>()
            .AddSingleton<TerraBase.Features.Backup.IProjectBackupDestinations, ProjectBackupDestinations>()
            .AddHostedService<BackupScheduler>()
            .AddSingleton<ScheduledExports>()
            .AddSingleton<IScheduledExports>(provider => provider.GetRequiredService<ScheduledExports>())
            .AddHostedService(provider => provider.GetRequiredService<ScheduledExports>());
}

namespace TerraBase.Features.Management.Backups;

public static class BackupsExtensions
{
    public static IServiceCollection AddTerraBaseManagementBackups(this IServiceCollection services) =>
        services
            .AddSingleton<IManagedBackupService, EngineManagedBackupService>()
            .AddSingleton<TerraBase.Features.Backup.IProjectBackupDestinations, ProjectBackupDestinations>()
            .AddHostedService<BackupScheduler>();
}

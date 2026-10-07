namespace TerraBase.Features.Management.Backups;

/// <summary>Registers database backups.</summary>
public static class BackupsExtensions
{
    /// <summary>Registers <see cref="IManagedBackupService"/> and the backup scheduler.</summary>
    public static IServiceCollection AddTerraBaseManagementBackups(this IServiceCollection services) =>
        services
            .AddSingleton<IManagedBackupService, EngineManagedBackupService>()
            .AddHostedService<BackupScheduler>();
}

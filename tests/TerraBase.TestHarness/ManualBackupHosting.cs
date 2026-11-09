using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TerraBase.Features.Backup;

namespace TerraBase.Testing;

internal static class ManualBackupHosting
{
    public static void Configure(IServiceCollection services)
    {
        var host = services.Single(descriptor => descriptor.ServiceType == typeof(IHostedService)
            && descriptor.ImplementationFactory?.Method.ReturnType is { } type && typeof(IContinuousBackup).IsAssignableFrom(type));
        services[services.IndexOf(host)] = ServiceDescriptor.Singleton<IHostedService, ManualBackupHost>();
        var purge = services.Single(descriptor => descriptor.ServiceType == typeof(IHostedService)
            && descriptor.ImplementationFactory?.Method.ReturnType == typeof(DatabaseBackupPurge));
        services.Remove(purge);
    }

    private sealed class ManualBackupHost(IContinuousBackup backup) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken)
        {
            ((ContinuousBackup)backup).PrepareScratch();
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => ((IHostedService)backup).StopAsync(cancellationToken);
    }
}

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TerraBase.Features.Backup;

namespace TerraBase.Testing;

internal static class ManualBackupHosting
{
    public static void Configure(IServiceCollection services)
    {
        // Only the test drives shipping. Keep the real service and its shutdown cleanup, without timer races.
        // BackupExtensions uses AddHostedService(provider => provider.GetRequiredService<ContinuousBackup>());
        // identify that factory by its return type, and keep this lookup in sync if registration changes shape.
        var host = services.Single(descriptor => descriptor.ServiceType == typeof(IHostedService)
            && descriptor.ImplementationFactory?.Method.ReturnType is { } type && typeof(IContinuousBackup).IsAssignableFrom(type));
        services[services.IndexOf(host)] = ServiceDescriptor.Singleton<IHostedService, ManualBackupHost>();
    }

    private sealed class ManualBackupHost(IContinuousBackup backup) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken) => ((IHostedService)backup).StopAsync(cancellationToken);
    }
}

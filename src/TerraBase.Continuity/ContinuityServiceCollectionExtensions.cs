using Microsoft.Extensions.DependencyInjection;
using TerraScale.QuicRpc;

namespace TerraBase.Continuity;

public static class ContinuityServiceCollectionExtensions
{
    extension(IServiceCollection services)
    {
        public IServiceCollection AddTerraBaseContinuity(ContinuityOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);
            services.AddSingleton(options.Validate());
            services.AddSingleton<ContinuityMetrics>();
            services.AddSingleton<S3ArchiveStore>();
            services.AddSingleton<ArchiveManifests>();
            services.AddSingleton<ContinuitySessions>();
            services.AddSingleton<DatabaseRestorer>();
            services.AddSingleton<BackupContinuityService>();
            services.AddSingleton<IBackupContinuity>(static provider => provider.GetRequiredService<BackupContinuityService>());
            services.AddSingleton<ChangefeedHub>();
            services.AddSingleton<IChangefeedHub>(static provider => provider.GetRequiredService<ChangefeedHub>());
            services.AddSingleton<IQuicEndpoints, ChangefeedQuicEndpoint>();
            services.AddHostedService<CommitLogArchiverService>();
            services.AddHostedService<ContinuityOperationWorker>();
            return services;
        }
    }
}

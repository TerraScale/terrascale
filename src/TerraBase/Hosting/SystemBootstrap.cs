using TerraBase.Cluster;
using TerraBase.Configuration;
using TerraBase.Engine;
using TerraBase.Features.Health;
using TerraBase.Features.Management;
using TerraBase.Features.Platform;
using ZLogger;

namespace TerraBase.Hosting;

internal sealed class SystemBootstrap(
    SystemDatabase system, PlatformCatalog platform, ManagementService management, ClusterOptions cluster, NodeReadiness readiness, IHostApplicationLifetime lifetime,
    ILogger<SystemBootstrap> logger)
    : IHostedService
{
    public ConfigurationError? Failure { get; private set; }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var opened = await system.OpenAsync(cluster.IsBootstrapNode, cancellationToken) switch
        {
            Done when cluster.IsBootstrapNode => await platform.EnsureAsync(cancellationToken),
            Done done => done,
            ConfigurationError error => error,
        };
        Failure = opened switch
        {
            Done => null,
            ConfigurationError error => error,
        };
        if (Failure is { } failure)
        {
            logger.ZLogCritical($"TerraBase could not open its system catalog: {failure.Message}");
            lifetime.StopApplication();
            return;
        }

        var (node, region, bootstrap) = (cluster.NodeId, cluster.Region, cluster.IsBootstrapNode);
        logger.ZLogInformation($"System catalog open on {node}.{region} (bootstrap node: {bootstrap})");
        var shredded = await management.SweepDeletedAsync(cancellationToken);
        if (shredded > 0)
        {
            logger.ZLogInformation($"Finished {shredded:@Shredded} interrupted database deletions");
        }

        lifetime.ApplicationStarted.Register(readiness.Ready);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
